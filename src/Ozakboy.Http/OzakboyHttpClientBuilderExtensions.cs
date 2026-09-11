using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Ozakboy.Http.Logging;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;
using Ozakboy.Security.Masking;

namespace Ozakboy.Http;

/// <summary>
/// 把本套件的處理器掛上 <see cref="IHttpClientBuilder"/>。
/// Attaches this package's handlers to an <see cref="IHttpClientBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>順序就是一切。</b><see cref="HttpClientFactoryServiceCollectionExtensions"/> 的管線是「先註冊的在外層」,
/// 因此依序掛上:重試(最外層)→ 限流 → 簽章 → 脫敏日誌(最內層)。原則有兩條:<b>每一次嘗試都是一個新請求</b>,
/// 以及<b>時間戳要是送出那一刻的</b>。
/// <b>Order is everything.</b> The <see cref="HttpClientFactoryServiceCollectionExtensions"/> pipeline puts the
/// first-registered handler outermost, so the handlers go on as: retry (outermost), rate limiting, signing,
/// sanitising logging (innermost). Two principles drive it: <b>every attempt is a new request</b>, and <b>a
/// timestamp must be the moment the request goes out</b>.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>重試在最外層</b>,讓其餘三段在每一次嘗試都重新執行一遍。退避等待發生在這一層,不持有任何限流許可。
/// <b>Retry is outermost</b>, so the other three run again on every attempt. Backoff waits happen at this level
/// and hold no rate-limit permit.
/// </description></item>
/// <item><description>
/// <b>限流在重試之內</b>:每次嘗試各付一份權重。放在重試外層只會被穿過一次,重試 N 次只付一份,
/// 本地配額低估實際用量 —— 對以權重計算封鎖(418)的服務,錯誤率一高正是最危險的時候。
/// 等待許可的時間不計入單次嘗試逾時(見 <see cref="RetryHandler"/>)。
/// <b>Rate limiting is inside retry</b>: every attempt pays its own weight. Outside retry it is traversed once,
/// so N retries pay for one and the local quota under-counts real usage — and for a service that bans by weight
/// (418), a rising error rate is exactly when that matters most. Time spent waiting for permits does not count
/// towards the attempt timeout (see <see cref="RetryHandler"/>).
/// </description></item>
/// <item><description>
/// <b>簽章在限流之內</b>:拿到許可之後才簽、才蓋時間戳。先簽再排隊,時間戳會在隊伍裡過期 ——
/// 限流等待上限預設 30 秒,幣安的 recvWindow 預設只有 5 秒。
/// <b>Signing is inside rate limiting</b>: the request is signed and stamped only once the permit is held. Sign
/// first and queue afterwards, and the timestamp ages in the queue — the limiter waits up to 30 seconds by
/// default, while Binance's recvWindow defaults to 5.
/// </description></item>
/// <item><description>
/// <b>日誌在最內層</b>:記下真正送出去的那一份(已放行、已簽章、第幾次嘗試)。
/// <b>Logging is innermost</b>: it records what actually went out — admitted, signed, and which attempt it was.
/// </description></item>
/// </list>
/// <para>
/// <b>這個順序是修了兩次才定下來的。</b>0.2.0 依「簽章 → 限流 → 重試 → 日誌」掛上,註解卻宣稱限流在重試外層所以
/// 「每次重試各自付權重」—— 推理剛好相反(外層只付一次),重試也因此沿用過期的時間戳。0.3.0 開發中先改成
/// 「重試 → 簽章 → 限流 → 日誌」,重試付權重、重新簽章都修好了,卻變成先簽章再排隊:排隊超過 recvWindow 的請求,
/// 一拿到許可送出去就被拒絕(<c>-1021</c>)。最後定為限流在簽章之外。換了順序不會有任何錯誤訊息,
/// 只會在執行期表現成難以理解的行為,因此每一點都有測試鎖住。
/// <b>This order took two fixes to settle.</b> 0.2.0 attached "signing, rate limiting, retry, logging" while
/// claiming that rate limiting outside retry made "each retry pay its own weight" — exactly backwards, since an
/// outer handler pays once — and retries went out with a stale timestamp. A 0.3.0 draft moved to "retry,
/// signing, rate limiting, logging": retries paid their weight and were re-signed, but requests were now signed
/// before queueing, so one that queued longer than recvWindow was rejected the moment it went out
/// (<c>-1021</c>). The final order puts rate limiting outside signing. A wrong order raises no error, only
/// hard-to-read runtime behaviour, so every one of these points is pinned by a test.
/// </para>
/// </remarks>
public static class OzakboyHttpClientBuilderExtensions
{
    /// <summary>
    /// 一次掛上整條管線,並把這個具名用戶端調整成適合它的狀態。
    /// Attaches the whole pipeline in one call and adjusts the named client to suit it.
    /// </summary>
    /// <param name="builder">用戶端建構器。The client builder.</param>
    /// <param name="configure">設定委派。The configuration delegate.</param>
    /// <returns>建構器本身。The builder.</returns>
    /// <remarks>
    /// <para>
    /// 除了四個處理器之外,這個方法還對這個具名用戶端做三件事:
    /// Besides the four handlers, this method does three things to the named client:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>登記祕密。</b><see cref="SigningOptions.ApiKey"/>、<see cref="SigningOptions.SecretKey"/> 與
    /// <see cref="HttpPipelineOptions.KnownSecrets"/> 登記到這個用戶端的遮罩器(以用戶端名稱為鍵的單例,
    /// 見 <see cref="OzakboyHttpServiceProviderExtensions.GetOzakboyHttpMasker"/>)。日誌、重試與錯誤都用它遮罩。
    /// 短於 <see cref="SecretMasker.MinimumKnownSecretLength"/> 的簽章金鑰無法登記(會大量誤遮一般文字),會被略過。
    /// <b>Registers secrets.</b> <see cref="SigningOptions.ApiKey"/>, <see cref="SigningOptions.SecretKey"/> and
    /// <see cref="HttpPipelineOptions.KnownSecrets"/> go onto this client's masker — a singleton keyed by client
    /// name, see <see cref="OzakboyHttpServiceProviderExtensions.GetOzakboyHttpMasker"/> — which logging, retry
    /// and errors all mask with. A signing key shorter than <see cref="SecretMasker.MinimumKnownSecretLength"/>
    /// cannot be registered (it would mask swathes of ordinary text) and is skipped.
    /// </description></item>
    /// <item><description>
    /// <b>移除 <see cref="IHttpClientFactory"/> 預設的日誌</b>(<c>RemoveAllLoggers()</c>)。預設的
    /// <c>LogicalHandler</c> / <c>ClientHandler</c> 日誌在 Information 層級寫出完整位址,.NET 只遮 query、不遮路徑;
    /// 對憑證放在路徑裡的服務(例如 Telegram 的 <c>/bot&lt;token&gt;/</c>),那就是直接外洩。
    /// 本套件自己的日誌處理器已經經過遮罩,不需要它們。
    /// <b>Removes <see cref="IHttpClientFactory"/>'s default logging</b> (<c>RemoveAllLoggers()</c>). The default
    /// <c>LogicalHandler</c> / <c>ClientHandler</c> logging writes the full URI at Information level, and .NET
    /// redacts only the query, not the path; for a service with the credential in the path (Telegram's
    /// <c>/bot&lt;token&gt;/</c>) that is a straight leak. This package's own logging handler is masked and makes
    /// them unnecessary.
    /// </description></item>
    /// <item><description>
    /// <b>把 <see cref="HttpClient.Timeout"/> 設為 <see cref="Timeout.InfiniteTimeSpan"/>。</b>逾時交給管線:
    /// 單次嘗試由重試處理器、整趟由 <see cref="HttpPipelineClient"/> 負責。預設的 100 秒若短於
    /// <see cref="HttpTimeoutOptions.OverallTimeout"/>,先觸發的會是它,而它表現成取消 ——
    /// 逾時就被錯歸為「呼叫端取消」,既不重試也不告警。呼叫端先前自行設定的 <see cref="HttpClient.Timeout"/> 會被覆蓋。
    /// <b>Sets <see cref="HttpClient.Timeout"/> to <see cref="Timeout.InfiniteTimeSpan"/>.</b> Timeouts belong to
    /// the pipeline: the retry handler bounds each attempt and <see cref="HttpPipelineClient"/> the whole
    /// exchange. The default 100 seconds, if shorter than <see cref="HttpTimeoutOptions.OverallTimeout"/>, fires
    /// first and surfaces as cancellation, so a timeout is misfiled as a caller cancellation — neither retried nor
    /// alerted on. Any <see cref="HttpClient.Timeout"/> the caller configured earlier is overridden.
    /// </description></item>
    /// </list>
    /// <para>
    /// 門面請以 <see cref="OzakboyHttpServiceProviderExtensions.CreateOzakboyHttpPipelineClient"/> 建立:
    /// 它帶入這個用戶端的遮罩器與同一份逾時設定。
    /// Build the facade with <see cref="OzakboyHttpServiceProviderExtensions.CreateOzakboyHttpPipelineClient"/>,
    /// which brings in this client's masker and the same timeout settings.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// 任一參數為 <see langword="null"/> 時擲出。Thrown when either argument is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定不合法時擲出。Thrown when the resulting options are invalid.
    /// </exception>
    public static IHttpClientBuilder AddOzakboyHttpPipeline(this IHttpClientBuilder builder, Action<HttpPipelineOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new HttpPipelineOptions();
        configure(options);

        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(configure));
        }

        var clientName = builder.Name;

        builder.Services.AddKeyedSingleton(clientName, (_, _) => new ClientSecretMasker(CreatePipelineMasker(options)));
        builder.Services.AddKeyedSingleton(clientName, (_, _) => new ClientPipelineRegistration(options.Timeouts));

        builder.RemoveAllLoggers();
        builder.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        // 以下的註冊順序就是管線順序(先註冊的在外層),理由見型別說明。
        // The registration order below is the pipeline order (first registered is outermost); see the type
        // remarks for why.
        builder.AddHttpMessageHandler(provider => new RetryHandler(
            options.Retry,
            options.Timeouts,
            provider.GetService<TimeProvider>(),
            ResolveMasker(provider, clientName)));

        if (options.EnableRateLimiting)
        {
            builder.AddWeightedRateLimiting(options.RateLimiting);
        }

        if (options.EnableSigning)
        {
            builder.AddHttpMessageHandler(provider => new SigningHandler(options.Signing, provider.GetService<TimeProvider>()));
        }

        builder.AddHttpMessageHandler(provider => new SanitizingLoggingHandler(
            CreateLogger(provider),
            options.Logging,
            provider.GetService<TimeProvider>(),
            ResolveMasker(provider, clientName)));

        return builder;
    }

    /// <summary>
    /// 只掛上簽章處理器。
    /// Attaches only the signing handler.
    /// </summary>
    /// <param name="builder">用戶端建構器。The client builder.</param>
    /// <param name="options">簽章設定。The signing options.</param>
    /// <returns>建構器本身。The builder.</returns>
    /// <remarks>
    /// 分段註冊時順序由呼叫端負責,請依型別說明的順序掛上:重試、限流、簽章、日誌。
    /// When registering sections individually the caller owns the order; follow the one in the type remarks:
    /// retry, rate limiting, signing, logging.
    /// </remarks>
    public static IHttpClientBuilder AddRequestSigning(this IHttpClientBuilder builder, SigningOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        return builder.AddHttpMessageHandler(provider => new SigningHandler(options, provider.GetService<TimeProvider>()));
    }

    /// <summary>
    /// 只掛上限流處理器。限流器以單例形式註冊,同一個用戶端名稱共用同一份配額。
    /// Attaches only the rate-limiting handler. The limiter is registered as a singleton, so every handler for
    /// the same client name shares one quota.
    /// </summary>
    /// <param name="builder">用戶端建構器。The client builder.</param>
    /// <param name="options">限流設定。The rate-limit options.</param>
    /// <returns>建構器本身。The builder.</returns>
    /// <remarks>
    /// 限流器必須是單例。<see cref="IHttpClientFactory"/> 會定期重建處理器鏈,
    /// 若每個處理器各自建一個限流器,配額會在每次重建後歸零 —— 症狀是「跑一陣子就被對方封鎖」,
    /// 而且本地看起來完全正常。
    /// The limiter has to be a singleton. <see cref="IHttpClientFactory"/> rebuilds the handler chain
    /// periodically, and a limiter created per handler resets its quota on every rebuild. The symptom is
    /// getting banned by the peer after a while, with everything looking perfectly fine locally.
    /// </remarks>
    public static IHttpClientBuilder AddWeightedRateLimiting(this IHttpClientBuilder builder, RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        var clientName = builder.Name;
        builder.Services.AddKeyedSingleton(
            clientName,
            (provider, _) => new WeightedRateLimiter(options, provider.GetService<TimeProvider>()));

        return builder.AddHttpMessageHandler(provider =>
            new RateLimitingHandler(provider.GetRequiredKeyedService<WeightedRateLimiter>(clientName)));
    }

    /// <summary>
    /// 只掛上重試處理器。
    /// Attaches only the retry handler.
    /// </summary>
    /// <param name="builder">用戶端建構器。The client builder.</param>
    /// <param name="options">重試設定。The retry options.</param>
    /// <param name="timeouts">逾時設定。The timeout options.</param>
    /// <returns>建構器本身。The builder.</returns>
    /// <remarks>
    /// 同一個用戶端名稱若也以 <see cref="AddSanitizedLogging"/> 註冊過,重試處理器產生的錯誤會用那一個遮罩器;
    /// 否則用 <see cref="SecretMasker.Default"/>。
    /// If the same client name was also registered with <see cref="AddSanitizedLogging"/>, errors raised by the
    /// retry handler are masked with that masker; otherwise with <see cref="SecretMasker.Default"/>.
    /// </remarks>
    public static IHttpClientBuilder AddRetry(this IHttpClientBuilder builder, RetryOptions options, HttpTimeoutOptions? timeouts = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        var clientName = builder.Name;
        return builder.AddHttpMessageHandler(provider =>
            new RetryHandler(
                options,
                timeouts,
                provider.GetService<TimeProvider>(),
                provider.GetKeyedService<ClientSecretMasker>(clientName)?.Masker));
    }

    /// <summary>
    /// 只掛上脫敏日誌處理器。
    /// Attaches only the sanitising logging handler.
    /// </summary>
    /// <param name="builder">用戶端建構器。The client builder.</param>
    /// <param name="options">日誌設定。The logging options.</param>
    /// <returns>建構器本身。The builder.</returns>
    /// <remarks>
    /// 遮罩器以用戶端名稱為鍵註冊成單例(若尚未註冊),可用
    /// <see cref="OzakboyHttpServiceProviderExtensions.GetOzakboyHttpMasker"/> 取得並登記祕密。
    /// 這個方法不會移除 <see cref="IHttpClientFactory"/> 的預設日誌;需要時請自行呼叫 <c>RemoveAllLoggers()</c>。
    /// The masker is registered as a singleton keyed by client name (unless one already is) and can be fetched
    /// with <see cref="OzakboyHttpServiceProviderExtensions.GetOzakboyHttpMasker"/> to register secrets. This method
    /// does not remove <see cref="IHttpClientFactory"/>'s default logging; call <c>RemoveAllLoggers()</c> yourself
    /// when needed.
    /// </remarks>
    public static IHttpClientBuilder AddSanitizedLogging(this IHttpClientBuilder builder, RequestLoggingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var clientName = builder.Name;
        var effectiveOptions = options ?? new RequestLoggingOptions();
        builder.Services.TryAddKeyedSingleton(clientName, (_, _) => new ClientSecretMasker(effectiveOptions.CreateMasker()));

        return builder.AddHttpMessageHandler(provider =>
            new SanitizingLoggingHandler(
                CreateLogger(provider),
                effectiveOptions,
                provider.GetService<TimeProvider>(),
                ResolveMasker(provider, clientName)));
    }

    private static SecretMasker CreatePipelineMasker(HttpPipelineOptions options)
    {
        var masker = options.Logging.CreateMasker();

        // 簽章金鑰一拿到就登記:它們之後從哪條路徑流出來(例外訊息、對方 echo 回的錯誤、自訂標頭),
        // 字面替換都攔得到,不必指望每條路徑都有欄位名可以判斷。
        // The signing keys are registered as soon as they are known: whichever path they later leak through —
        // an exception message, an error echoed back by the peer, a custom header — literal replacement catches
        // them, without relying on every path offering a field name to judge by.
        RegisterIfUsable(masker, options.Signing.ApiKey);
        RegisterIfUsable(masker, options.Signing.SecretKey);

        foreach (var secret in options.KnownSecrets)
        {
            masker.RegisterKnownSecret(secret);
        }

        return masker;
    }

    private static void RegisterIfUsable(SecretMasker masker, string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= SecretMasker.MinimumKnownSecretLength)
        {
            masker.RegisterKnownSecret(secret);
        }
    }

    private static SecretMasker ResolveMasker(IServiceProvider provider, string clientName) =>
        provider.GetRequiredKeyedService<ClientSecretMasker>(clientName).Masker;

    private static ILogger CreateLogger(IServiceProvider provider) =>
        provider.GetService<ILoggerFactory>()?.CreateLogger<SanitizingLoggingHandler>()
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SanitizingLoggingHandler>.Instance;
}

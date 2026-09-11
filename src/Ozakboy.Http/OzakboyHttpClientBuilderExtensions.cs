using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ozakboy.Http.Logging;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;

namespace Ozakboy.Http;

/// <summary>
/// 把本套件的處理器掛上 <see cref="IHttpClientBuilder"/>。
/// Attaches this package's handlers to an <see cref="IHttpClientBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>順序就是一切。</b><see cref="HttpClientFactoryServiceCollectionExtensions"/> 的管線是「先註冊的在外層」,
/// 因此必須依序掛上:簽章 → 限流 → 重試 → 脫敏日誌。
/// 換了順序不會有任何錯誤訊息,只會在執行期表現成難以理解的行為:
/// 簽章若排在重試內層,每次重試都會重新簽一次(時間戳變了,某些服務會直接拒絕);
/// 限流若排在重試內層,重試風暴就不算權重,對方的配額會被打穿;
/// 日誌若不在最內層,記到的是「打算送出的內容」而不是真正送出去的那一份。
/// <b>Order is everything.</b> The <see cref="HttpClientFactoryServiceCollectionExtensions"/> pipeline puts
/// the first-registered handler outermost, so they must go on in this sequence: signing, rate limiting, retry,
/// sanitising logging. Getting it wrong produces no error at all, only behaviour that is hard to read at
/// runtime: signing inside retry re-signs on every attempt with a fresh timestamp, which some services reject
/// outright; rate limiting inside retry lets a retry storm consume no weight and blow through the peer's
/// quota; and logging anywhere but innermost records what was meant to be sent rather than what was.
/// </para>
/// </remarks>
public static class OzakboyHttpClientBuilderExtensions
{
    /// <summary>
    /// 一次掛上整條管線。
    /// Attaches the whole pipeline in one call.
    /// </summary>
    /// <param name="builder">用戶端建構器。The client builder.</param>
    /// <param name="configure">設定委派。The configuration delegate.</param>
    /// <returns>建構器本身。The builder.</returns>
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

        if (options.EnableSigning)
        {
            builder.AddHttpMessageHandler(_ => new SigningHandler(options.Signing));
        }

        if (options.EnableRateLimiting)
        {
            builder.AddWeightedRateLimiting(options.RateLimiting);
        }

        builder.AddHttpMessageHandler(provider => new RetryHandler(
            options.Retry,
            options.Timeouts,
            provider.GetService<TimeProvider>()));

        builder.AddHttpMessageHandler(provider => new SanitizingLoggingHandler(
            CreateLogger(provider),
            options.Logging,
            provider.GetService<TimeProvider>()));

        return builder;
    }

    /// <summary>
    /// 只掛上簽章處理器。
    /// Attaches only the signing handler.
    /// </summary>
    /// <param name="builder">用戶端建構器。The client builder.</param>
    /// <param name="options">簽章設定。The signing options.</param>
    /// <returns>建構器本身。The builder.</returns>
    public static IHttpClientBuilder AddRequestSigning(this IHttpClientBuilder builder, SigningOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        return builder.AddHttpMessageHandler(_ => new SigningHandler(options));
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
    public static IHttpClientBuilder AddRetry(this IHttpClientBuilder builder, RetryOptions options, HttpTimeoutOptions? timeouts = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        return builder.AddHttpMessageHandler(provider =>
            new RetryHandler(options, timeouts, provider.GetService<TimeProvider>()));
    }

    /// <summary>
    /// 只掛上脫敏日誌處理器。
    /// Attaches only the sanitising logging handler.
    /// </summary>
    /// <param name="builder">用戶端建構器。The client builder.</param>
    /// <param name="options">日誌設定。The logging options.</param>
    /// <returns>建構器本身。The builder.</returns>
    public static IHttpClientBuilder AddSanitizedLogging(this IHttpClientBuilder builder, RequestLoggingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddHttpMessageHandler(provider =>
            new SanitizingLoggingHandler(CreateLogger(provider), options, provider.GetService<TimeProvider>()));
    }

    private static ILogger CreateLogger(IServiceProvider provider) =>
        provider.GetService<ILoggerFactory>()?.CreateLogger<SanitizingLoggingHandler>()
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SanitizingLoggingHandler>.Instance;
}

using Microsoft.Extensions.DependencyInjection;
using Ozakboy.Security.Masking;

namespace Ozakboy.Http;

/// <summary>
/// 從服務容器取得具名用戶端所用的遮罩器,用來登記執行期才取得的祕密,或交給 <see cref="HttpPipelineClient"/>。
/// Retrieves the masker a named client uses, for registering secrets obtained at run time or handing to
/// <see cref="HttpPipelineClient"/>.
/// </summary>
public static class OzakboyHttpServiceProviderExtensions
{
    /// <summary>
    /// 取得具名用戶端的遮罩器。
    /// Gets the masker for a named client.
    /// </summary>
    /// <param name="provider">服務容器。The service provider.</param>
    /// <param name="clientName">用戶端名稱,與 <c>AddHttpClient</c> 時相同。The client name used with <c>AddHttpClient</c>.</param>
    /// <returns>
    /// 這個用戶端的日誌處理器、重試處理器共用的那一個遮罩器;在它上面登記的祕密立即對兩者生效。
    /// The one masker shared by this client's logging and retry handlers; a secret registered on it takes effect
    /// for both at once.
    /// </returns>
    /// <remarks>
    /// <para>
    /// 兩種用途。其一,執行期才取得的祕密(例如幣安使用者資料串流的 listenKey,位址形如
    /// <c>wss://host/ws/&lt;listenKey&gt;</c>)在這裡以 <see cref="SecretMasker.RegisterKnownSecret"/> 登記。
    /// 其二,自行建立 <see cref="HttpPipelineClient"/> 時把它傳進去,錯誤離開管線的最後一道關口才會用同一份清單遮罩;
    /// 沒傳的話,那道關口只認得 <see cref="SecretMasker.Default"/> 上的祕密。
    /// Two uses. First, secrets obtained at run time — a Binance user-data-stream listenKey, whose address looks
    /// like <c>wss://host/ws/&lt;listenKey&gt;</c> — are registered here with
    /// <see cref="SecretMasker.RegisterKnownSecret"/>. Second, pass it to an <see cref="HttpPipelineClient"/> you
    /// construct yourself, so the last checkpoint an error passes on its way out masks against the same list;
    /// without it, that checkpoint knows only the secrets on <see cref="SecretMasker.Default"/>.
    /// </para>
    /// <para>
    /// 設定階段就已知的祕密,直接放進 <see cref="HttpPipelineOptions.KnownSecrets"/> 即可。
    /// Secrets already known at configuration time go straight into <see cref="HttpPipelineOptions.KnownSecrets"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="provider"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="provider"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="clientName"/> 為 <see langword="null"/> 或空白時擲出。
    /// Thrown when <paramref name="clientName"/> is <see langword="null"/> or blank.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// 這個名稱沒有以 <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/> 或
    /// <see cref="OzakboyHttpClientBuilderExtensions.AddSanitizedLogging"/> 註冊過時擲出。
    /// Thrown when no client of that name was registered through
    /// <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/> or
    /// <see cref="OzakboyHttpClientBuilderExtensions.AddSanitizedLogging"/>.
    /// </exception>
    public static SecretMasker GetOzakboyHttpMasker(this IServiceProvider provider, string clientName)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        var holder = provider.GetKeyedService<ClientSecretMasker>(clientName)
            ?? throw new InvalidOperationException(
                "這個用戶端名稱沒有以 AddOzakboyHttpPipeline 或 AddSanitizedLogging 註冊過,沒有對應的遮罩器。No masker exists for this client name; it was not registered through AddOzakboyHttpPipeline or AddSanitizedLogging.");

        return holder.Masker;
    }

    /// <summary>
    /// 為以 <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/> 註冊的具名用戶端建立
    /// <see cref="HttpPipelineClient"/>,自動帶入該用戶端的遮罩器、同一份逾時設定與容器裡的時間來源。
    /// 這是建立門面的建議做法。
    /// Creates an <see cref="HttpPipelineClient"/> for a named client registered with
    /// <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/>, bringing in that client's masker,
    /// the same timeout settings, and the container's time source. This is the recommended way to build the facade.
    /// </summary>
    /// <param name="provider">服務容器。The service provider.</param>
    /// <param name="clientName">用戶端名稱,與 <c>AddHttpClient</c> 時相同。The client name used with <c>AddHttpClient</c>.</param>
    /// <returns>新的門面。A new facade.</returns>
    /// <remarks>
    /// <para>
    /// 門面是錯誤離開本套件前的最後一道關口,用的是建構時傳入的遮罩器。手動 <c>new</c> 時很容易漏傳,
    /// 漏了也不會有任何錯誤 —— 那道關口只是默默地只認得 <see cref="SecretMasker.Default"/> 上的祕密。
    /// 整體逾時也一樣:手動傳入的逾時設定可能與管線裡重試處理器用的那份不一致。這個方法把兩者都從註冊處取回。
    /// The facade is the last checkpoint errors pass on their way out, and it masks with the masker it was
    /// constructed with. Building it by hand makes that easy to forget, and forgetting raises no error — the
    /// checkpoint just quietly knows only the secrets on <see cref="SecretMasker.Default"/>. The overall timeout is
    /// the same story: timeouts passed by hand can drift from the ones the pipeline's retry handler uses. This
    /// method takes both from the registration.
    /// </para>
    /// <para>
    /// 做成服務容器上的方法、而不是再註冊一個服務,是讓生命週期留給呼叫端決定(通常是
    /// <c>services.AddSingleton(p =&gt; p.CreateOzakboyHttpPipelineClient("exchange"))</c>),
    /// 多個具名用戶端也不必各自註冊成帶鍵的 <see cref="HttpPipelineClient"/>。
    /// It is a method on the service provider rather than another service registration so that the lifetime stays
    /// the caller's choice (usually <c>services.AddSingleton(p =&gt; p.CreateOzakboyHttpPipelineClient("exchange"))</c>),
    /// and several named clients need no keyed <see cref="HttpPipelineClient"/> registrations of their own.
    /// </para>
    /// <para>
    /// <b>門面註冊成單例是可以的,因為它不持有 <see cref="HttpClient"/>。</b>這個方法交給門面的是
    /// <see cref="IHttpClientFactory"/> 與用戶端名稱,門面每一次請求才各取一個用戶端。差別在於
    /// <c>SetHandlerLifetime</c>(預設兩分鐘)的處理器輪替只在每次 <c>CreateClient</c> 時才有機會發生:
    /// 若門面在建構時取一個 <see cref="HttpClient"/> 拿著不放,輪替就永遠輪不到,對方換 IP 之後 DNS 跟不上,
    /// 症狀只會在長時間無人值守的執行中出現。
    /// <b>Registering the facade as a singleton is fine because it holds no <see cref="HttpClient"/>.</b> This
    /// method hands the facade the <see cref="IHttpClientFactory"/> and the client name, and the facade takes a
    /// client per request. What turns on this is that handler rotation under <c>SetHandlerLifetime</c> (two
    /// minutes by default) only gets its chance on each <c>CreateClient</c> call: a facade that took one
    /// <see cref="HttpClient"/> at construction and held on to it would never rotate, so DNS could not keep up
    /// once the peer moved to a new IP — a symptom that surfaces only on long unattended runs.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="provider"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="provider"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="clientName"/> 為 <see langword="null"/> 或空白時擲出。
    /// Thrown when <paramref name="clientName"/> is <see langword="null"/> or blank.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// 這個名稱沒有以 <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/> 註冊過時擲出。
    /// Thrown when no client of that name was registered through
    /// <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/>.
    /// </exception>
    public static HttpPipelineClient CreateOzakboyHttpPipelineClient(this IServiceProvider provider, string clientName)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        var registration = provider.GetKeyedService<ClientPipelineRegistration>(clientName)
            ?? throw new InvalidOperationException(
                "這個用戶端名稱沒有以 AddOzakboyHttpPipeline 註冊過,無法建立門面。No client of this name was registered through AddOzakboyHttpPipeline, so no facade can be built.");

        // 交出工廠而不是工廠建出來的用戶端:門面通常被註冊成單例,拿著同一個 HttpClient 不放會讓處理器永不輪替。
        // The factory is handed over rather than a client built from it: the facade is usually registered as a
        // singleton, and holding one HttpClient for its whole life would stop the handlers ever rotating.
        return new HttpPipelineClient(
            provider.GetRequiredService<IHttpClientFactory>(),
            clientName,
            registration.Timeouts,
            provider.GetService<TimeProvider>(),
            provider.GetOzakboyHttpMasker(clientName));
    }
}

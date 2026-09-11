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
}

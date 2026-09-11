using Ozakboy.Security.Masking;

namespace Ozakboy.Http;

/// <summary>
/// 以用戶端名稱為鍵、註冊成單例的遮罩器容器。同一個具名用戶端的日誌、重試與錯誤邊界共用它。
/// A container for the masker, registered as a singleton keyed by client name. The logging, retry and error
/// boundary of one named client all share it.
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要單例:<see cref="System.Net.Http.IHttpClientFactory"/> 會定期重建處理器鏈。若遮罩器掛在處理器實例上,
/// 執行期登記的祕密會在下一次重建後消失 —— 而且沒有任何跡象。
/// Why a singleton: <see cref="System.Net.Http.IHttpClientFactory"/> rebuilds handler chains periodically. A
/// masker held by a handler instance would lose every secret registered at run time on the next rebuild, with
/// nothing to show for it.
/// </para>
/// <para>
/// 為什麼包一層而不直接以 <see cref="SecretMasker"/> 註冊:以用戶端名稱當鍵的 <see cref="SecretMasker"/>
/// 很容易和別的套件或應用程式自己的註冊撞在一起;專屬的容器型別讓鍵只在本套件內有意義。
/// Why a wrapper rather than registering <see cref="SecretMasker"/> directly: a <see cref="SecretMasker"/>
/// keyed by client name could easily collide with another package's registration or the application's own; a
/// dedicated container type keeps the key meaningful only inside this package.
/// </para>
/// </remarks>
internal sealed class ClientSecretMasker
{
    public ClientSecretMasker(SecretMasker masker)
    {
        ArgumentNullException.ThrowIfNull(masker);
        Masker = masker;
    }

    public SecretMasker Masker { get; }
}

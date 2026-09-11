namespace Ozakboy.Http.Signing;

/// <summary>
/// 已簽名的參數放在請求的哪個位置。
/// Where the signed parameters are placed in the request.
/// </summary>
public enum SignedPayloadPlacement
{
    /// <summary>
    /// 放在 query 字串。適用於 <c>GET</c>,多數服務的 <c>POST</c> 也接受。
    /// In the query string. Suitable for <c>GET</c>, and accepted for <c>POST</c> by most services.
    /// </summary>
    QueryString = 0,

    /// <summary>
    /// 放在 <c>application/x-www-form-urlencoded</c> 的請求主體。
    /// 注意此時原本的 <see cref="HttpRequestMessage.Content"/> 會被取代。
    /// In an <c>application/x-www-form-urlencoded</c> request body. Note that this replaces any existing
    /// <see cref="HttpRequestMessage.Content"/>.
    /// </summary>
    FormBody = 1,
}

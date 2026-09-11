using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http.Signing;

/// <summary>
/// 簽章處理器的設定。
/// Options for the signing handler.
/// </summary>
/// <remarks>
/// 這裡的預設值刻意保持中性,不綁定任何特定服務。金鑰標頭名與簽章參數名各家不同
/// (例如有的用 <c>X-MBX-APIKEY</c>),由呼叫端依對接對象設定。
/// The defaults here are deliberately neutral and tied to no particular service. The API-key header name and
/// the signature parameter name differ between providers (some use <c>X-MBX-APIKEY</c>), so callers set them
/// to match whatever they are talking to.
/// </remarks>
public sealed class SigningOptions
{
    /// <summary>
    /// API 金鑰。會以標頭送出(見 <see cref="ApiKeyHeaderName"/>),不參與簽章計算。
    /// The API key, sent as a header (see <see cref="ApiKeyHeaderName"/>). It is not part of the signature.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 簽章金鑰。絕不寫入日誌,也絕不出現在請求內容中。
    /// The signing secret. It is never logged and never appears in the request.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// 攜帶 API 金鑰的標頭名稱。預設 <c>X-API-Key</c>。
    /// The header that carries the API key. Defaults to <c>X-API-Key</c>.
    /// </summary>
    public string ApiKeyHeaderName { get; set; } = "X-API-Key";

    /// <summary>
    /// 簽章參數的名稱,會附加在待簽字串之後。預設 <c>signature</c>。
    /// The name of the signature parameter appended after the canonical string. Defaults to <c>signature</c>.
    /// </summary>
    public string SignatureParameterName { get; set; } = "signature";

    /// <summary>
    /// 是否隨請求送出 API 金鑰標頭。
    /// Whether to send the API-key header with the request.
    /// </summary>
    public bool SendApiKeyHeader { get; set; } = true;

    /// <summary>
    /// 已簽名的參數放在 query 或請求主體。
    /// Whether the signed parameters go in the query string or the request body.
    /// </summary>
    public SignedPayloadPlacement Placement { get; set; } = SignedPayloadPlacement.QueryString;

    /// <summary>
    /// 簽章演算法,預設為 <see cref="HmacSha256SignatureAlgorithm"/>。
    /// The signing algorithm; defaults to <see cref="HmacSha256SignatureAlgorithm"/>.
    /// </summary>
    public ISignatureAlgorithm Algorithm { get; set; } = HmacSha256SignatureAlgorithm.Instance;

    /// <summary>
    /// 檢查設定是否可用。
    /// Validates the options.
    /// </summary>
    /// <returns>
    /// 設定可用時為成功;否則為帶 <see cref="ErrorCategory.Validation"/> 的失敗。
    /// Success when the options are usable, otherwise a failure carrying <see cref="ErrorCategory.Validation"/>.
    /// </returns>
    public Result Validate()
    {
        if (Algorithm is null)
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "未設定簽章演算法。No signing algorithm is configured.");
        }

        if (string.IsNullOrWhiteSpace(SignatureParameterName))
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "簽章參數名不可為空白。The signature parameter name must not be blank.");
        }

        if (SendApiKeyHeader && string.IsNullOrWhiteSpace(ApiKeyHeaderName))
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "API 金鑰標頭名不可為空白。The API key header name must not be blank.");
        }

        return Result.Success();
    }
}

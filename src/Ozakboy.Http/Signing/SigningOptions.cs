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
    /// 每次簽章時要重新蓋上當下時間(Unix 毫秒)的參數名,例如幣安的 <c>timestamp</c>。
    /// 預設 <see langword="null"/>:不動任何參數。
    /// The parameter to restamp with the current time (Unix milliseconds) on every signing, such as Binance's
    /// <c>timestamp</c>. Defaults to <see langword="null"/>: no parameter is touched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只重新簽章不夠。時間戳若是呼叫端組請求時就寫死在參數裡,每次重試重新算出的簽章仍然簽著同一個舊時間 ——
    /// 退避一久,重試本身就會被對方的時間窗拒絕(幣安 <c>-1021</c>)。設定這個名稱之後,簽章處理器會在每一次嘗試
    /// 以 <see cref="TimeProvider"/> 的當下時間取代它(位置不變;原本沒有則附加在尾端),再計算簽章。
    /// Re-signing alone is not enough. If the timestamp was fixed in the parameters when the caller built the
    /// request, every retry's fresh signature still signs the same old time, and after a long backoff the retry
    /// itself is rejected by the peer's time window (Binance <c>-1021</c>). With this name set, the signing
    /// handler replaces that parameter with the <see cref="TimeProvider"/>'s current time on every attempt (in
    /// place, or appended when absent) before computing the signature.
    /// </para>
    /// <para>
    /// 只作用於標記了 <see cref="HttpRequestMessageExtensions.WithSignature"/> 的請求。
    /// Applies only to requests marked with <see cref="HttpRequestMessageExtensions.WithSignature"/>.
    /// </para>
    /// </remarks>
    public string? TimestampParameterName { get; set; }

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

        if (TimestampParameterName is not null && string.IsNullOrWhiteSpace(TimestampParameterName))
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "時間戳參數名不可為空白;不需要時請設為 null。The timestamp parameter name must not be blank; set it to null when unused.");
        }

        return Result.Success();
    }
}

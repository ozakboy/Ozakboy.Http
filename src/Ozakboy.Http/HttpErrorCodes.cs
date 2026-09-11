using System.Globalization;
using System.Net;

namespace Ozakboy.Http;

/// <summary>
/// 本套件產生的錯誤代碼常數。呼叫端可據此分支,不必比對錯誤訊息字串。
/// The error codes this package produces. Callers can branch on these instead of matching message text.
/// </summary>
/// <remarks>
/// 代碼一律小寫、以點分段,前段是來源(<c>http</c>)、後段是原因。這些字串屬於公開契約的一部分,
/// 一旦發佈就不再更動 —— 呼叫端的重試判斷與監控告警都可能綁在上面。
/// Codes are lowercase and dot-segmented: the first segment is the source (<c>http</c>) and the rest is the
/// reason. These strings are part of the public contract and do not change once published, because callers'
/// retry logic and monitoring alerts may key on them.
/// </remarks>
public static class HttpErrorCodes
{
    /// <summary>
    /// 整體逾時:含重試在內的整趟請求超過允許時間。
    /// The overall timeout: the whole exchange, retries included, exceeded the allowed time.
    /// </summary>
    public const string Timeout = "http.timeout";

    /// <summary>
    /// 單次嘗試逾時。仍可能有後續重試。
    /// A single attempt timed out; further retries may still follow.
    /// </summary>
    public const string AttemptTimeout = "http.attempt_timeout";

    /// <summary>
    /// 連線層失敗:DNS、TCP、TLS 或連線中斷。
    /// A transport-level failure: DNS, TCP, TLS, or a dropped connection.
    /// </summary>
    public const string Network = "http.network";

    /// <summary>
    /// 呼叫端主動取消。這不是失敗,但仍以錯誤形式回報以便統一處理。
    /// The caller cancelled. Not a fault, but reported as an error so callers handle one shape only.
    /// </summary>
    public const string Cancelled = "http.cancelled";

    /// <summary>
    /// 對方回應限流(HTTP 429)。
    /// The peer signalled rate limiting (HTTP 429).
    /// </summary>
    public const string RateLimited = "http.rate_limited";

    /// <summary>
    /// 本地限流器在允許時間內排不到額度。請求從未送出。
    /// The local rate limiter could not obtain permits in time. The request was never sent.
    /// </summary>
    public const string RateLimitTimeout = "http.rate_limit.timeout";

    /// <summary>
    /// 請求宣告的權重超過某個配額桶的上限,無論等多久都不可能通過。
    /// The declared weight exceeds a bucket's limit, so no amount of waiting can satisfy it.
    /// </summary>
    public const string RateLimitWeightTooLarge = "http.rate_limit.weight_too_large";

    /// <summary>
    /// 請求需要簽章,但未提供簽章金鑰。
    /// The request requires a signature but no secret key was supplied.
    /// </summary>
    public const string SigningSecretMissing = "http.signing.secret_missing";

    /// <summary>
    /// 簽章計算失敗。
    /// Computing the signature failed.
    /// </summary>
    public const string SigningFailed = "http.signing.failed";

    /// <summary>
    /// 請求沒有目標位址,無法組出待簽字串。
    /// The request has no target URI, so no canonical string can be built.
    /// </summary>
    public const string SigningMissingRequestUri = "http.signing.missing_request_uri";

    /// <summary>
    /// 設定值不合法。
    /// The supplied options are invalid.
    /// </summary>
    public const string InvalidOptions = "http.options.invalid";

    /// <summary>
    /// HTTP 狀態碼錯誤的代碼前綴,完整代碼形如 <c>http.status.429</c>。
    /// The prefix for status-code errors; a full code looks like <c>http.status.429</c>.
    /// </summary>
    public const string StatusPrefix = "http.status.";

    /// <summary>
    /// 組出某個 HTTP 狀態碼對應的錯誤代碼。
    /// Builds the error code for a given HTTP status code.
    /// </summary>
    /// <param name="statusCode">HTTP 狀態碼。The HTTP status code.</param>
    /// <returns>形如 <c>http.status.503</c> 的代碼。A code such as <c>http.status.503</c>.</returns>
    public static string ForStatus(HttpStatusCode statusCode) =>
        StatusPrefix + ((int)statusCode).ToString(CultureInfo.InvariantCulture);
}

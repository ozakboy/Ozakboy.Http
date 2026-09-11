namespace Ozakboy.Http;

/// <summary>
/// 本套件寫進 <see cref="Ozakboy.Core.Abstractions.Error.Data"/> 的資料鍵常數。
/// The keys this package writes into <see cref="Ozakboy.Core.Abstractions.Error.Data"/>.
/// </summary>
/// <remarks>
/// <para>
/// 這些鍵與 <see cref="HttpErrorCodes"/> 一樣屬於公開契約:重試判斷、監控告警與下游的錯誤處理都可能綁在
/// 上面,發佈後就不再更動。取值請用 <c>Error.TryGetInt64</c> / <c>TryGetDecimal</c> / <c>TryGetData</c>,
/// 不要自己 <c>Parse</c> —— 型別化的取法已經內建 <see cref="System.Globalization.CultureInfo.InvariantCulture"/>。
/// Like <see cref="HttpErrorCodes"/>, these keys are part of the public contract: retry logic, monitoring
/// alerts, and downstream error handling may all key on them, and they do not change once published. Read
/// them with <c>Error.TryGetInt64</c> / <c>TryGetDecimal</c> / <c>TryGetData</c> rather than parsing by hand;
/// the typed accessors already pin <see cref="System.Globalization.CultureInfo.InvariantCulture"/>.
/// </para>
/// </remarks>
public static class HttpErrorDataKeys
{
    /// <summary>
    /// HTTP 狀態碼,以 <c>TryGetInt64</c> 讀回。
    /// The HTTP status code; read it back with <c>TryGetInt64</c>.
    /// </summary>
    public const string StatusCode = "statusCode";

    /// <summary>
    /// 錯誤回應的內容摘要,以 <c>TryGetData</c> 讀回。
    /// A snippet of the error response body; read it back with <c>TryGetData</c>.
    /// </summary>
    public const string Body = "body";

    /// <summary>
    /// 對方 <c>Retry-After</c> 指示的秒數,以 <c>TryGetDecimal</c> 讀回。
    /// The number of seconds the peer's <c>Retry-After</c> asked for; read it back with <c>TryGetDecimal</c>.
    /// </summary>
    /// <remarks>
    /// 存的是對方原本說的長度,<b>沒有</b>套用 <see cref="Retry.RetryOptions.MaxRetryAfter"/> 的上限 ——
    /// 上限是本地的處置決定,不該改寫對方講過的話。想知道「等了多久」看的是實際退避,想知道
    /// 「對方要求等多久」才看這個值。
    /// This holds what the peer actually said, with <see cref="Retry.RetryOptions.MaxRetryAfter"/> <b>not</b>
    /// applied: the ceiling is a local handling decision and should not rewrite the peer's statement. The
    /// effective backoff answers "how long did we wait"; this value answers "how long were we asked to wait".
    /// </remarks>
    public const string RetryAfterSeconds = "retryAfterSeconds";

    /// <summary>
    /// 放棄之前一共送出幾次嘗試,以 <c>TryGetInt64</c> 讀回。僅出現在
    /// <see cref="Ozakboy.Core.Abstractions.ErrorCategory.Exhausted"/> 的錯誤上。
    /// How many attempts went out before giving up; read it back with <c>TryGetInt64</c>. Only present on
    /// <see cref="Ozakboy.Core.Abstractions.ErrorCategory.Exhausted"/> errors.
    /// </summary>
    public const string Attempts = "attempts";
}

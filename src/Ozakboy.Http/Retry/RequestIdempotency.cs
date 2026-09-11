namespace Ozakboy.Http.Retry;

/// <summary>
/// 這個請求重送是否安全。
/// Whether this request is safe to resend.
/// </summary>
/// <remarks>
/// 之所以要有一個「未宣告」的狀態,而不是用 <see cref="bool"/>,是為了讓「可以重試」永遠是一個
/// 看得見的決定。用布林值時,預設值 <see langword="false"/> 與「有人想過並決定不重試」長得一模一樣,
/// 程式碼審查時分不出來。
/// The point of having an "undeclared" state instead of a <see cref="bool"/> is to keep "may be retried" a
/// visible decision. With a boolean, the default <see langword="false"/> is indistinguishable from someone
/// having thought about it and decided against retrying, which a code review cannot tell apart.
/// </remarks>
public enum RequestIdempotency
{
    /// <summary>
    /// 未宣告,由 HTTP 方法推定:只有安全方法(<c>GET</c>、<c>HEAD</c>、<c>OPTIONS</c>、<c>TRACE</c>)才重試。
    /// Undeclared; inferred from the HTTP method — only safe methods (<c>GET</c>, <c>HEAD</c>, <c>OPTIONS</c>,
    /// <c>TRACE</c>) are retried.
    /// </summary>
    Inferred = 0,

    /// <summary>
    /// 明確宣告為冪等,允許重試。
    /// Explicitly declared idempotent; retries are allowed.
    /// </summary>
    Idempotent = 1,

    /// <summary>
    /// 明確宣告為非冪等,禁止重試。
    /// Explicitly declared non-idempotent; retries are forbidden.
    /// </summary>
    NonIdempotent = 2,
}

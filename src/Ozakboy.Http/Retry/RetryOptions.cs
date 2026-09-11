using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http.Retry;

/// <summary>
/// 重試處理器的設定。
/// Options for the retry handler.
/// </summary>
public sealed class RetryOptions
{
    /// <summary>
    /// 重試策略:次數、退避形狀與抖動。退避間隔一律由策略計算,不在處理器裡另算一套。
    /// The retry policy: attempt count, backoff shape, and jitter. Delays are always computed by the policy,
    /// never recomputed inside the handler.
    /// </summary>
    /// <remarks>
    /// 策略沒有設定 <see cref="RetryPolicy.RetryPredicate"/> 時,重試處理器另有一條規則:本地限流逾時
    /// (<see cref="HttpErrorCodes.RateLimitTimeout"/>)不重試 —— 請求從未送出,且已經等滿了限流器的上限,
    /// 再試只會把等待乘上嘗試次數。要重試它,請在 <see cref="RetryPolicy.RetryPredicate"/> 裡明說。
    /// When the policy sets no <see cref="RetryPolicy.RetryPredicate"/>, the retry handler applies one more rule:
    /// a local rate-limit timeout (<see cref="HttpErrorCodes.RateLimitTimeout"/>) is not retried — the request never
    /// went out and has already waited out the limiter's ceiling, so another attempt only multiplies the wait. To
    /// retry it anyway, say so in a <see cref="RetryPolicy.RetryPredicate"/>.
    /// </remarks>
    public RetryPolicy Policy { get; set; } = RetryPolicy.Default;

    /// <summary>
    /// 回應帶 <c>Retry-After</c> 時是否改用它指定的等待時間。
    /// Whether a <c>Retry-After</c> header overrides the computed backoff.
    /// </summary>
    public bool RespectRetryAfter { get; set; } = true;

    /// <summary>
    /// 尊重 <c>Retry-After</c> 的上限。對方給出誇張的數字時不至於把呼叫端掛住。
    /// The ceiling applied to <c>Retry-After</c>, so an outlandish value from the peer cannot hang the caller.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 讀取錯誤回應內容作為診斷摘要的最大長度;設為 0 表示不讀取。
    /// The maximum length of the error-response snippet kept for diagnostics; 0 disables reading the body.
    /// </summary>
    public int ErrorBodySnippetLength { get; set; }

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
        if (Policy is null)
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "未設定重試策略。No retry policy is configured.");
        }

        if (MaxRetryAfter <= TimeSpan.Zero)
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "Retry-After 上限必須為正值。The Retry-After ceiling must be positive.");
        }

        if (ErrorBodySnippetLength < 0)
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "錯誤內容摘要長度不可為負。The error snippet length must not be negative.");
        }

        return Result.Success();
    }
}

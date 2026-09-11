using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http.RateLimiting;

/// <summary>
/// 加權限流器的設定。
/// Options for the weighted rate limiter.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>
    /// 配額桶清單。全部必須同時滿足才會放行。
    /// The quota buckets. A request is admitted only when every bucket can satisfy it.
    /// </summary>
    public IList<RateLimitBucket> Buckets { get; } = [];

    /// <summary>
    /// 請求未宣告權重時採用的預設值。
    /// The weight used when a request declares none.
    /// </summary>
    public int DefaultWeight { get; set; } = 1;

    /// <summary>
    /// 等待額度的上限。超過就放棄並回報限流失敗,請求不會送出。
    /// How long to wait for permits. Past this the limiter gives up and reports a rate-limit failure; the
    /// request is never sent.
    /// </summary>
    /// <remarks>
    /// 設得太長會讓呼叫端在完全不知情的情況下卡住,設得太短則在正常尖峰就開始丟請求。
    /// 交易系統偏好前者失敗得快一點 —— 一筆遲到太久的訂單通常已經沒有意義。
    /// Too long and callers stall with no idea why; too short and ordinary bursts start failing. Trading
    /// systems generally prefer to fail fast here: an order that arrives far too late is usually worthless.
    /// </remarks>
    public TimeSpan AcquisitionTimeout { get; set; } = TimeSpan.FromSeconds(30);

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
        if (Buckets.Count == 0)
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "至少要設定一個配額桶。At least one quota bucket must be configured.");
        }

        if (Buckets.Any(bucket => bucket is null))
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "配額桶不可為 null。Quota buckets must not be null.");
        }

        if (DefaultWeight < 1)
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "預設權重必須為正整數。The default weight must be positive.");
        }

        if (AcquisitionTimeout <= TimeSpan.Zero)
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "等待額度的上限必須為正值。The acquisition timeout must be positive.");
        }

        var smallest = Buckets.Min(bucket => bucket.PermitLimit);
        if (DefaultWeight > smallest)
        {
            return Error.Validation(
                HttpErrorCodes.InvalidOptions,
                $"預設權重 {DefaultWeight} 超過最小配額桶的上限 {smallest},任何請求都無法通過。The default weight {DefaultWeight} exceeds the smallest bucket limit {smallest}, so no request could ever pass.");
        }

        return Result.Success();
    }
}

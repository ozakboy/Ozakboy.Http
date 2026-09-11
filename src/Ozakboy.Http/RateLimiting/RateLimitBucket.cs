namespace Ozakboy.Http.RateLimiting;

/// <summary>
/// 一個配額桶:在某個時間窗內最多消耗多少權重。
/// One quota bucket: how much weight may be consumed within a given time window.
/// </summary>
/// <remarks>
/// 交易所通常同時有多個時間窗的配額(例如「每分鐘 2400 權重」與「每秒 300 權重」),
/// 而且不同端點消耗的權重不同。多個桶必須「同時」滿足,任一桶不足就得等。
/// Exchanges typically enforce several windows at once — "2400 weight per minute" alongside "300 weight per
/// second" — and different endpoints consume different amounts. All buckets must be satisfied simultaneously;
/// if any one is short, the request waits.
/// </remarks>
public sealed record RateLimitBucket
{
    /// <summary>
    /// 建立配額桶。
    /// Creates a quota bucket.
    /// </summary>
    /// <param name="name">桶名稱,用於日誌與診斷。The bucket name, used for logging and diagnostics.</param>
    /// <param name="permitLimit">時間窗內可用的權重總量,必須為正整數。The weight available per window; must be positive.</param>
    /// <param name="window">時間窗長度,必須為正值。The window length; must be positive.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> 為空白時擲出。Thrown when <paramref name="name"/> is blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="permitLimit"/> 小於 1,或 <paramref name="window"/> 不為正值時擲出。
    /// Thrown when <paramref name="permitLimit"/> is less than 1 or <paramref name="window"/> is not positive.
    /// </exception>
    public RateLimitBucket(string name, int permitLimit, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        Name = name;
        PermitLimit = permitLimit;
        Window = window;
    }

    /// <summary>
    /// 桶名稱。
    /// The bucket name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 時間窗內可用的權重總量。
    /// The weight available within each window.
    /// </summary>
    public int PermitLimit { get; }

    /// <summary>
    /// 時間窗長度。
    /// The window length.
    /// </summary>
    public TimeSpan Window { get; }
}

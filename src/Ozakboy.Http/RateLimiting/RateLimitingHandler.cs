using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http.RateLimiting;

/// <summary>
/// 在請求送出前取得限流額度。排不到就直接失敗,請求不會離開本機。
/// Acquires rate-limit permits before the request goes out. When none can be had the request fails here and
/// never leaves the machine.
/// </summary>
/// <remarks>
/// 位置在簽章之後、重試之前。放在重試外層是刻意的:每一次重試都應該各自付出自己的權重,
/// 否則重試風暴會在對方的配額上炸開,換來更長的封鎖。
/// It sits after signing and before retry. Being outside the retry handler is deliberate: each retry should
/// pay its own weight, otherwise a retry storm blows through the peer's quota and earns a longer ban.
/// </remarks>
public sealed class RateLimitingHandler : DelegatingHandler
{
    private readonly WeightedRateLimiter _limiter;
    private readonly bool _ownsLimiter;

    /// <summary>
    /// 以既有的限流器建立處理器。
    /// Creates the handler around an existing limiter.
    /// </summary>
    /// <param name="limiter">限流器。通常是整個用戶端共用的單一實例。The limiter, usually one instance shared by the whole client.</param>
    /// <param name="ownsLimiter">
    /// 處理器被釋放時是否一併釋放限流器。共用實例請保持 <see langword="false"/>。
    /// Whether disposing the handler also disposes the limiter. Keep <see langword="false"/> for a shared one.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="limiter"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="limiter"/> is <see langword="null"/>.
    /// </exception>
    public RateLimitingHandler(WeightedRateLimiter limiter, bool ownsLimiter = false)
    {
        ArgumentNullException.ThrowIfNull(limiter);

        _limiter = limiter;
        _ownsLimiter = ownsLimiter;
    }

    /// <summary>
    /// 以設定建立處理器,並自行持有限流器。
    /// Creates the handler with its own limiter built from options.
    /// </summary>
    /// <param name="options">限流設定。The rate-limit options.</param>
    /// <param name="timeProvider">時間來源。The time source.</param>
    /// <remarks>
    /// 這個建構式適合單一用戶端的情境。若同一份配額被多個用戶端共用,請自行建立
    /// <see cref="WeightedRateLimiter"/> 並用另一個建構式傳入,否則每個處理器各算各的,配額會被超用。
    /// This constructor suits a single client. When several clients share one quota, build the
    /// <see cref="WeightedRateLimiter"/> yourself and pass it to the other constructor; otherwise each handler
    /// counts separately and the quota is overrun.
    /// </remarks>
    public RateLimitingHandler(RateLimitOptions options, TimeProvider? timeProvider = null)
        : this(new WeightedRateLimiter(options, timeProvider), ownsLimiter: true)
    {
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var weight = request.GetWeight() ?? _limiter.DefaultWeight;
        var acquisition = await _limiter.AcquireAsync(weight, cancellationToken).ConfigureAwait(false);
        if (acquisition.IsFailure)
        {
            throw acquisition.Error!.ToException();
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsLimiter)
        {
            _limiter.Dispose();
        }

        base.Dispose(disposing);
    }
}

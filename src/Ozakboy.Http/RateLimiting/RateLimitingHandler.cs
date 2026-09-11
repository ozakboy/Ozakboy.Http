using Ozakboy.Core.Abstractions;
using Ozakboy.Http.Retry;

namespace Ozakboy.Http.RateLimiting;

/// <summary>
/// 在請求送出前取得限流額度。排不到就直接失敗,請求不會離開本機。
/// Acquires rate-limit permits before the request goes out. When none can be had the request fails here and
/// never leaves the machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>位置在重試之內。</b><see cref="System.Net.Http.IHttpClientFactory"/> 的管線是「先註冊的在外層」,
/// 重試處理器在外層時,每一次嘗試都會重新穿過這裡,各自付出自己的權重 —— 這才對得上對方的算法:
/// 幣安這類服務以實際收到的請求計算權重,超過就回 418 封鎖位址。錯誤率一高,重試就是實打實的額外權重。
/// <b>It sits inside retry.</b> The <see cref="System.Net.Http.IHttpClientFactory"/> pipeline puts the
/// first-registered handler outermost, so with retry outside, every attempt passes through here again and pays
/// its own weight. That matches the peer's arithmetic: services such as Binance count the requests they actually
/// receive and answer 418 — an address ban — past the limit, and when the error rate climbs, retries are very
/// real extra weight.
/// </para>
/// <para>
/// <b>位置在簽章之外。</b>拿到許可之後,簽章處理器才簽章、蓋時間戳。先簽再排隊,時間戳就在隊伍裡過期:
/// 等待上限(<see cref="RateLimitOptions.AcquisitionTimeout"/>)預設 30 秒,幣安的 recvWindow 預設只有 5 秒。
/// 0.3.0 開發中曾把簽章放在這裡之前,排隊超過 recvWindow 的請求一出去就被拒絕(<c>-1021</c>)。
/// <b>It sits outside signing.</b> The signing handler signs and stamps the request only after the permit is
/// held. Sign first and queue afterwards, and the timestamp ages in the queue: the wait ceiling
/// (<see cref="RateLimitOptions.AcquisitionTimeout"/>) defaults to 30 seconds, while Binance's recvWindow defaults
/// to 5. A 0.3.0 draft put signing ahead of this handler, and a request that queued longer than recvWindow was
/// rejected the moment it went out (<c>-1021</c>).
/// </para>
/// <para>
/// <b>等待許可的時間不計入單次嘗試逾時。</b>等待期間暫停重試處理器掛上的單次計時器,拿到許可後從頭起算;
/// 等待本身由 <see cref="RateLimitOptions.AcquisitionTimeout"/> 與呼叫端的整體逾時約束。
/// <b>Time spent waiting for permits does not count towards the attempt timeout.</b> The per-attempt timer the
/// retry handler attached is paused during the wait and restarted from zero once the permit is held; the wait
/// itself is bounded by <see cref="RateLimitOptions.AcquisitionTimeout"/> and the caller's overall timeout.
/// </para>
/// <para>
/// 0.2.0 的註解寫著「放在重試外層,每次重試各自付權重」,推理剛好相反:外層只會被穿過一次,
/// 同一個請求重試 N 次只付一份權重,本地配額因此低估了實際用量。
/// The 0.2.0 comment read "outside retry, so each retry pays its own weight", which is exactly backwards:
/// an outer handler is traversed once, so a request retried N times paid for one, and the local quota
/// under-counted real usage.
/// </para>
/// <para>
/// 重試的退避等待發生在外層的重試處理器裡,那時這裡的閘門早已釋放,不持有任何許可;
/// 權杖桶的權重是「取走」而不是「借用」,所以也沒有要歸還的東西。
/// Retry backoff happens in the outer retry handler, by which time this handler's gate has long been released
/// and nothing is held; token-bucket weight is taken rather than borrowed, so there is nothing to give back
/// either.
/// </para>
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

        // 排隊不算單次嘗試的時間:等待期間暫停計時,拿到許可後從頭起算。沒有重試處理器時沒有計時器,什麼都不做。
        // Queueing is not attempt time: the clock pauses during the wait and restarts once the permit is held.
        // Without a retry handler there is no timer and nothing happens.
        var attemptTimer = AttemptTimer.Find(request);
        attemptTimer?.Pause();

        var acquisition = await _limiter.AcquireAsync(weight, cancellationToken).ConfigureAwait(false);
        if (acquisition.IsFailure)
        {
            throw acquisition.Error.ToException();
        }

        attemptTimer?.Restart();

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

namespace Ozakboy.Http.Retry;

/// <summary>
/// 單次嘗試的逾時計時器。由重試處理器建立並掛在該次嘗試的請求上,限流處理器等待許可時暫停它、拿到許可後從頭起算。
/// The per-attempt timeout clock. The retry handler creates it and attaches it to the attempt's request; the
/// rate-limiting handler pauses it while waiting for permits and restarts it from zero once they are granted.
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要能暫停:單次嘗試逾時量的是「對方多久沒回應」,不是「本地排了多久隊」。限流等待若計入,
/// 單次逾時(幣安預設 10 秒)短於限流等待上限(30 秒)時,排隊超過 10 秒的請求就會變成逾時、被重試,
/// 重新排到隊伍最後面 —— 越擠越排不到。排隊本身已經有限流器自己的上限與呼叫端的整體逾時約束。
/// Why it pauses: the attempt timeout measures how long the peer takes to answer, not how long the request
/// queued locally. Counting the queue would, whenever the attempt timeout (10 seconds by Binance's default) is
/// shorter than the limiter's ceiling (30 seconds), turn any request that queued past 10 seconds into a timeout
/// that is retried at the back of the queue — the busier it gets, the less anything gets through. The queue is
/// already bounded by the limiter's own ceiling and the caller's overall timeout.
/// </para>
/// <para>
/// 沒有限流處理器的管線裡沒有人暫停它,計時就從嘗試開始時起算,與 0.2.0 相同。
/// In a pipeline with no rate-limiting handler nothing pauses it, and the clock runs from the start of the
/// attempt, as in 0.2.0.
/// </para>
/// </remarks>
internal sealed class AttemptTimer : IDisposable
{
    private static readonly HttpRequestOptionsKey<AttemptTimer> OptionsKey = new("Ozakboy.Http.AttemptTimer");

    private readonly CancellationTokenSource _source;

    public AttemptTimer(TimeSpan timeout, TimeProvider timeProvider)
    {
        Timeout = timeout;
        _source = new CancellationTokenSource(timeout, timeProvider);
    }

    public TimeSpan Timeout { get; }

    public CancellationToken Token => _source.Token;

    public bool HasExpired => _source.IsCancellationRequested;

    public static AttemptTimer? Find(HttpRequestMessage request) =>
        request.Options.TryGetValue(OptionsKey, out var timer) ? timer : null;

    public static void Attach(HttpRequestMessage request, AttemptTimer timer) => request.Options.Set(OptionsKey, timer);

    public static void Detach(HttpRequestMessage request) =>
        ((IDictionary<string, object?>)request.Options).Remove(OptionsKey.Key);

    /// <summary>
    /// 暫停計時。已經到期的不受影響。
    /// Pauses the clock. One that has already expired is left as it is.
    /// </summary>
    public void Pause() => Change(System.Threading.Timeout.InfiniteTimeSpan);

    /// <summary>
    /// 從現在起重新計時完整的一段逾時。
    /// Restarts the clock for a full timeout from now.
    /// </summary>
    public void Restart() => Change(Timeout);

    public void Dispose() => _source.Dispose();

    private void Change(TimeSpan delay)
    {
        if (_source.IsCancellationRequested)
        {
            return;
        }

        try
        {
            // 以 TimeProvider 建立的 CancellationTokenSource,CancelAfter 會改動同一個計時器,假時鐘下同樣可測。
            // On a CancellationTokenSource created with a TimeProvider, CancelAfter reschedules that same timer,
            // so it stays testable under a fake clock.
            _source.CancelAfter(delay);
        }
        catch (ObjectDisposedException)
        {
            // 嘗試已結束、計時器已釋放:沒有什麼需要再計時的了。
            // The attempt is over and the timer disposed: there is nothing left to time.
        }
    }
}

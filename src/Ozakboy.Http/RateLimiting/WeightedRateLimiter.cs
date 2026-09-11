using System.Globalization;
using System.Threading.RateLimiting;
using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http.RateLimiting;

/// <summary>
/// 多桶加權限流器:每個請求宣告自己的權重,所有配額桶都足夠時才放行。
/// A multi-bucket weighted rate limiter: each request declares its own weight and is admitted only when every
/// quota bucket can cover it.
/// </summary>
/// <remarks>
/// <para>
/// <b>配額計算交給官方原語。</b>每個桶背後都是一個
/// <see cref="TokenBucketRateLimiter"/>,權重的扣減、可用量查詢與執行緒安全全部由它負責,
/// 這裡不自行實作任何限流演算法。
/// <b>The quota arithmetic belongs to the official primitive.</b> Each bucket is a
/// <see cref="TokenBucketRateLimiter"/>; deducting weight, reporting availability, and thread safety are all
/// its job. No rate-limiting algorithm is implemented here.
/// </para>
/// <para>
/// <b>但時間由 <see cref="TimeProvider"/> 決定。</b><see cref="TokenBucketRateLimiter"/> 內建的自動補充
/// 綁死在真實時鐘上(即使關掉自動補充,<see cref="TokenBucketRateLimiter.TryReplenish"/> 仍以真實經過時間為閘),
/// 用假時鐘測不出多桶行為 —— 而限流正是最需要確定性測試的部分。
/// 因此這裡把補充週期設為一個 tick(等於解除它的內部時間閘),改由本類別依 <see cref="TimeProvider"/>
/// 判斷時間窗是否到期、再呼叫 <see cref="TokenBucketRateLimiter.TryReplenish"/>。
/// 官方原語仍然負責配額,本類別只負責「什麼時候該補」。
/// <b>Time, however, comes from <see cref="TimeProvider"/>.</b> The limiter's built-in replenishment is tied to
/// the real clock — even with auto-replenishment off, <see cref="TokenBucketRateLimiter.TryReplenish"/> is
/// still gated on real elapsed time — which makes multi-bucket behaviour untestable under a fake clock, and
/// rate limiting is exactly where deterministic tests matter most. The replenishment period is therefore set
/// to a single tick, which disarms that internal gate, and this class decides from
/// <see cref="TimeProvider"/> when a window has elapsed before calling
/// <see cref="TokenBucketRateLimiter.TryReplenish"/>. The official primitive still owns the quota; this class
/// owns only the question of when to refill.
/// </para>
/// <para>
/// <b>放行決策是序列化的。</b>權杖桶的租約在釋放時不會歸還權重,所以「先取甲桶、乙桶不足」會漏掉甲桶的權重。
/// 這裡以一個閘門把決策序列化:先確認所有桶都夠,再一次取走。副作用是請求依抵達順序放行,這在交易系統
/// 反而是想要的 —— 先送出的訂單先出去。
/// <b>Admission is serialised.</b> Token-bucket leases do not return their permits on release, so acquiring
/// from bucket A and then failing on bucket B would leak A's weight. A gate serialises the decision instead:
/// confirm every bucket has room, then take from all of them at once. The side effect is that requests are
/// admitted in arrival order, which is what a trading system wants anyway — the order submitted first goes out
/// first.
/// </para>
/// </remarks>
public sealed class WeightedRateLimiter : IDisposable
{
    private readonly Bucket[] _buckets;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _acquisitionTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// 建立限流器。
    /// Creates the limiter.
    /// </summary>
    /// <param name="options">限流設定。The rate-limit options.</param>
    /// <param name="timeProvider">
    /// 時間來源。傳入 <see langword="null"/> 時使用 <see cref="TimeProvider.System"/>;測試請傳入假時鐘。
    /// The time source. <see langword="null"/> means <see cref="TimeProvider.System"/>; tests pass a fake clock.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定不合法時擲出。Thrown when the options are invalid.
    /// </exception>
    public WeightedRateLimiter(RateLimitOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _acquisitionTimeout = options.AcquisitionTimeout;
        DefaultWeight = options.DefaultWeight;

        var now = _timeProvider.GetUtcNow();
        _buckets = [.. options.Buckets.Select(definition => new Bucket(definition, now))];
        MaxWeight = _buckets.Min(bucket => bucket.Definition.PermitLimit);
    }

    /// <summary>
    /// 請求未宣告權重時採用的預設值。
    /// The weight used when a request declares none.
    /// </summary>
    public int DefaultWeight { get; }

    /// <summary>
    /// 單一請求可宣告的最大權重,等於最小配額桶的上限。
    /// The largest weight a single request may declare, equal to the smallest bucket's limit.
    /// </summary>
    public int MaxWeight { get; }

    /// <summary>
    /// 取得權重額度,必要時等待到下一個時間窗。
    /// Acquires weight, waiting for the next window when necessary.
    /// </summary>
    /// <param name="weight">這個請求消耗的權重。The weight this request consumes.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 取得額度時為成功;權重不合法、等待逾時或被取消時為失敗。
    /// Success once permits are held; a failure when the weight is invalid, the wait times out, or the caller
    /// cancels.
    /// </returns>
    public async Task<Result> AcquireAsync(int weight, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (weight < 1)
        {
            return Error.Validation(
                HttpErrorCodes.RateLimitWeightTooLarge,
                "請求權重必須為正整數。The request weight must be positive.");
        }

        if (weight > MaxWeight)
        {
            return Error.Validation(
                HttpErrorCodes.RateLimitWeightTooLarge,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"請求權重 {weight} 超過最小配額桶的上限 {MaxWeight},等多久都不會通過。The request weight {weight} exceeds the smallest bucket limit {MaxWeight}; no amount of waiting can satisfy it."));
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            return Cancelled(exception);
        }

        try
        {
            var deadline = _timeProvider.GetUtcNow() + _acquisitionTimeout;

            while (true)
            {
                var now = _timeProvider.GetUtcNow();
                ReplenishDueBuckets(now);

                if (CanAdmit(weight))
                {
                    Consume(weight);
                    return Result.Success();
                }

                var wait = TimeUntilNextWindow(now);
                if (now + wait > deadline)
                {
                    return Error.RateLimited(
                        HttpErrorCodes.RateLimitTimeout,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"本地限流在 {_acquisitionTimeout} 內排不到 {weight} 權重,請求未送出。The local rate limiter could not obtain {weight} weight within {_acquisitionTimeout}; the request was not sent."));
                }

                try
                {
                    await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                {
                    return Cancelled(exception);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 釋放限流器持有的資源。
    /// Releases the resources held by the limiter.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var bucket in _buckets)
        {
            bucket.Limiter.Dispose();
        }

        _gate.Dispose();
    }

    private static Result Cancelled(OperationCanceledException exception) =>
        new Error(HttpErrorCodes.Cancelled, "等待限流額度時被取消。Cancelled while waiting for rate-limit permits.", ErrorCategory.Cancelled)
        {
            // 本套件產生的錯誤一律不帶原始例外物件(見 SanitizedException)。這裡拿不到用戶端的遮罩器,
            // 先以預設遮罩器替換;錯誤離開管線時,邊界會再以用戶端的遮罩器遮一次。
            // Errors from this package never carry the original exception object (see SanitizedException).
            // The client's masker is not reachable here, so the default one is used; the boundary masks again
            // with the client's masker when the error leaves the pipeline.
            Exception = ErrorSanitizer.Sanitize(exception, Ozakboy.Security.Masking.SecretMasker.Default),
        };

    private void ReplenishDueBuckets(DateTimeOffset now)
    {
        foreach (var bucket in _buckets)
        {
            if (now < bucket.NextRefillAt)
            {
                continue;
            }

            bucket.Limiter.TryReplenish();

            // 停機或長時間閒置後可能已經跨過很多個時間窗,直接對齊到下一個未來的邊界,
            // 免得接下來連續補好幾次(補充是「補滿」,重複補沒有意義)。
            // After downtime or a long idle stretch several windows may have passed; realign straight to the
            // next future boundary instead of refilling repeatedly, since a refill tops the bucket up anyway.
            var window = bucket.Definition.Window;
            var elapsed = now - bucket.NextRefillAt;
            var skipped = (long)(elapsed.Ticks / window.Ticks) + 1;
            bucket.NextRefillAt += new TimeSpan(window.Ticks * skipped);
        }
    }

    private bool CanAdmit(int weight) =>
        _buckets.All(bucket => bucket.Limiter.GetStatistics()?.CurrentAvailablePermits >= weight);

    private void Consume(int weight)
    {
        foreach (var bucket in _buckets)
        {
            // 上一行已確認每個桶都夠,而決策在閘門內序列化,因此這裡必定成功。
            // 權杖桶的租約釋放時不歸還權重,所以取得後立即釋放即可。
            // The check above confirmed every bucket has room and the decision is serialised behind the gate,
            // so this cannot fail. Token-bucket leases do not return permits, so releasing at once is fine.
            bucket.Limiter.AttemptAcquire(weight).Dispose();
        }
    }

    private TimeSpan TimeUntilNextWindow(DateTimeOffset now)
    {
        var earliest = _buckets.Min(bucket => bucket.NextRefillAt);
        var wait = earliest - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    private sealed class Bucket
    {
        public Bucket(RateLimitBucket definition, DateTimeOffset now)
        {
            Definition = definition;
            NextRefillAt = now + definition.Window;
            Limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = definition.PermitLimit,
                TokensPerPeriod = definition.PermitLimit,

                // 這個週期刻意設成最小值:它的作用是解除原語內部以真實時鐘為準的補充閘,
                // 讓「何時該補」完全由外層的 TimeProvider 決定(見型別說明)。
                // The period is deliberately the smallest possible value: its only job is to disarm the
                // primitive's real-clock replenishment gate so the outer TimeProvider decides when to refill
                // (see the type remarks).
                ReplenishmentPeriod = TimeSpan.FromTicks(1),
                AutoReplenishment = false,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
        }

        public RateLimitBucket Definition { get; }

        public TokenBucketRateLimiter Limiter { get; }

        public DateTimeOffset NextRefillAt { get; set; }
    }
}

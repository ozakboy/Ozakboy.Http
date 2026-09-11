using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Tests.TestSupport;

namespace Ozakboy.Http.Tests;

/// <summary>
/// 限流行為一律以假時鐘驗證,測試中沒有任何 <c>Thread.Sleep</c> 或真實等待。
/// Rate-limit behaviour is verified against a fake clock; these tests contain no <c>Thread.Sleep</c> and no
/// real waiting.
/// </summary>
[TestClass]
public sealed class WeightedRateLimiterTests
{
    [TestMethod]
    public async Task AcquireAsync_WithinTheBucketLimit_SucceedsImmediately()
    {
        var clock = new FakeTimeProvider();
        using var limiter = CreateLimiter(clock, new RateLimitBucket("minute", 10, TimeSpan.FromMinutes(1)));

        for (var i = 0; i < 10; i++)
        {
            var result = await limiter.AcquireAsync(1, CancellationToken.None);
            Assert.IsTrue(result.IsSuccess, $"第 {i + 1} 次取得額度不應失敗。Acquisition {i + 1} should not fail.");
        }
    }

    [TestMethod]
    public async Task AcquireAsync_AfterTheWindowElapses_PermitsAreRefilled()
    {
        var clock = new FakeTimeProvider();
        using var limiter = CreateLimiter(clock, new RateLimitBucket("minute", 2, TimeSpan.FromMinutes(1)));

        Assert.IsTrue((await limiter.AcquireAsync(2, CancellationToken.None)).IsSuccess);

        // 額度已用盡:此刻再要就必須等到下一個時間窗。
        // The bucket is empty, so the next request must wait for the following window.
        var pending = limiter.AcquireAsync(2, CancellationToken.None);
        Assert.IsFalse(pending.IsCompleted, "額度用盡時不該立刻完成。It must not complete while the bucket is empty.");

        var result = await FakeClockRunner.RunAsync(clock, pending, TimeSpan.FromSeconds(10));

        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task AcquireAsync_MultipleBuckets_TheTighterOneGates()
    {
        // 每分鐘 100、每秒 5:一秒內連拿 5 次沒問題,第 6 次必須等到下一秒,
        // 即使分鐘桶還剩 95 的額度。
        // 100 per minute and 5 per second: five in one second is fine, the sixth has to wait for the next
        // second even though the minute bucket still has 95 to spare.
        var clock = new FakeTimeProvider();
        using var limiter = CreateLimiter(
            clock,
            new RateLimitBucket("minute", 100, TimeSpan.FromMinutes(1)),
            new RateLimitBucket("second", 5, TimeSpan.FromSeconds(1)));

        for (var i = 0; i < 5; i++)
        {
            Assert.IsTrue((await limiter.AcquireAsync(1, CancellationToken.None)).IsSuccess);
        }

        var sixth = limiter.AcquireAsync(1, CancellationToken.None);
        Assert.IsFalse(sixth.IsCompleted, "每秒桶已滿,第 6 次應該要等。The per-second bucket is full, so the sixth must wait.");

        var result = await FakeClockRunner.RunAsync(clock, sixth, TimeSpan.FromMilliseconds(200));
        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task AcquireAsync_HeavyRequest_ConsumesItsDeclaredWeight()
    {
        var clock = new FakeTimeProvider();
        using var limiter = CreateLimiter(clock, new RateLimitBucket("minute", 10, TimeSpan.FromMinutes(1)));

        Assert.IsTrue((await limiter.AcquireAsync(8, CancellationToken.None)).IsSuccess);
        Assert.IsTrue((await limiter.AcquireAsync(2, CancellationToken.None)).IsSuccess);

        var blocked = limiter.AcquireAsync(1, CancellationToken.None);
        Assert.IsFalse(blocked.IsCompleted, "權重 8 + 2 已把 10 用完。A weight of 8 plus 2 exhausts the limit of 10.");

        Assert.IsTrue((await FakeClockRunner.RunAsync(clock, blocked, TimeSpan.FromSeconds(10))).IsSuccess);
    }

    [TestMethod]
    public async Task AcquireAsync_WeightLargerThanTheSmallestBucket_FailsWithoutWaiting()
    {
        var clock = new FakeTimeProvider();
        using var limiter = CreateLimiter(
            clock,
            new RateLimitBucket("minute", 100, TimeSpan.FromMinutes(1)),
            new RateLimitBucket("second", 5, TimeSpan.FromSeconds(1)));

        var result = await limiter.AcquireAsync(6, CancellationToken.None);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(HttpErrorCodes.RateLimitWeightTooLarge, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Validation, result.Error!.Category);
        Assert.IsFalse(result.Error!.IsTransient, "等多久都不會通過的請求不該被判為暫時性。A request that can never pass must not be classified transient.");
    }

    [TestMethod]
    public async Task AcquireAsync_NonPositiveWeight_Fails()
    {
        var clock = new FakeTimeProvider();
        using var limiter = CreateLimiter(clock, new RateLimitBucket("minute", 10, TimeSpan.FromMinutes(1)));

        var result = await limiter.AcquireAsync(0, CancellationToken.None);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(HttpErrorCodes.RateLimitWeightTooLarge, result.Error!.Code);
    }

    [TestMethod]
    public async Task AcquireAsync_WaitExceedsTheAcquisitionTimeout_FailsAsRateLimited()
    {
        var clock = new FakeTimeProvider();
        var options = new RateLimitOptions { AcquisitionTimeout = TimeSpan.FromSeconds(5) };
        options.Buckets.Add(new RateLimitBucket("hour", 1, TimeSpan.FromHours(1)));
        using var limiter = new WeightedRateLimiter(options, clock);

        Assert.IsTrue((await limiter.AcquireAsync(1, CancellationToken.None)).IsSuccess);

        // 下一個時間窗在一小時後,遠超過 5 秒的等待上限,因此應立刻放棄而不是傻等。
        // The next window is an hour away, far beyond the five-second ceiling, so it gives up at once rather
        // than waiting it out.
        var result = await limiter.AcquireAsync(1, CancellationToken.None);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(HttpErrorCodes.RateLimitTimeout, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.RateLimited, result.Error!.Category);
        Assert.IsTrue(result.Error!.IsTransient, "本地限流放棄屬暫時性,稍後重來是合理的。Giving up locally is transient; coming back later is reasonable.");
    }

    [TestMethod]
    public async Task AcquireAsync_CancelledWhileWaiting_FailsAsCancelled()
    {
        var clock = new FakeTimeProvider();
        using var limiter = CreateLimiter(clock, new RateLimitBucket("minute", 1, TimeSpan.FromMinutes(1)));
        using var cancellation = new CancellationTokenSource();

        Assert.IsTrue((await limiter.AcquireAsync(1, CancellationToken.None)).IsSuccess);

        var pending = limiter.AcquireAsync(1, cancellation.Token);
        await cancellation.CancelAsync();
        var result = await pending;

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(HttpErrorCodes.Cancelled, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Cancelled, result.Error!.Category);
    }

    [TestMethod]
    public async Task AcquireAsync_LongIdlePeriod_DoesNotAccumulateExtraPermits()
    {
        // 停機好幾個時間窗之後回來,額度應該只回到上限,不該累積成好幾倍 ——
        // 那會在重啟後瞬間打爆對方的配額。
        // Coming back after several windows of downtime should restore the limit, not a multiple of it; the
        // latter would blow through the peer's quota the moment the process restarts.
        var clock = new FakeTimeProvider();
        using var limiter = CreateLimiter(clock, new RateLimitBucket("minute", 3, TimeSpan.FromMinutes(1)));

        Assert.IsTrue((await limiter.AcquireAsync(3, CancellationToken.None)).IsSuccess);

        clock.Advance(TimeSpan.FromHours(5));

        Assert.IsTrue((await limiter.AcquireAsync(3, CancellationToken.None)).IsSuccess);

        var overflow = limiter.AcquireAsync(1, CancellationToken.None);
        Assert.IsFalse(overflow.IsCompleted, "閒置再久,單一時間窗的額度上限仍是 3。However long it idled, one window still allows only 3.");

        Assert.IsTrue((await FakeClockRunner.RunAsync(clock, overflow, TimeSpan.FromSeconds(10))).IsSuccess);
    }

    [TestMethod]
    public void MaxWeight_IsTheSmallestBucketLimit()
    {
        var clock = new FakeTimeProvider();
        using var limiter = CreateLimiter(
            clock,
            new RateLimitBucket("minute", 100, TimeSpan.FromMinutes(1)),
            new RateLimitBucket("second", 5, TimeSpan.FromSeconds(1)));

        Assert.AreEqual(5, limiter.MaxWeight);
    }

    [TestMethod]
    public async Task AcquireAsync_AfterDispose_Throws()
    {
        var clock = new FakeTimeProvider();
        var limiter = CreateLimiter(clock, new RateLimitBucket("minute", 10, TimeSpan.FromMinutes(1)));
        limiter.Dispose();
        limiter.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => limiter.AcquireAsync(1, CancellationToken.None));
    }

    [TestMethod]
    public void Constructor_NoBuckets_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => new WeightedRateLimiter(new RateLimitOptions()));

    [TestMethod]
    public void Constructor_NullOptions_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => new WeightedRateLimiter(null!));

    [TestMethod]
    public void Bucket_NonPositiveLimit_Throws() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RateLimitBucket("x", 0, TimeSpan.FromSeconds(1)));

    [TestMethod]
    public void Bucket_NonPositiveWindow_Throws() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RateLimitBucket("x", 1, TimeSpan.Zero));

    [TestMethod]
    public void Bucket_BlankName_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => new RateLimitBucket(" ", 1, TimeSpan.FromSeconds(1)));

    private static WeightedRateLimiter CreateLimiter(TimeProvider clock, params RateLimitBucket[] buckets)
    {
        var options = new RateLimitOptions { AcquisitionTimeout = TimeSpan.FromHours(24) };
        foreach (var bucket in buckets)
        {
            options.Buckets.Add(bucket);
        }

        return new WeightedRateLimiter(options, clock);
    }
}

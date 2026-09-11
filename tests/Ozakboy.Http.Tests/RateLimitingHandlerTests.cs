using System.Net;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Tests.TestSupport;

namespace Ozakboy.Http.Tests;

[TestClass]
public sealed class RateLimitingHandlerTests
{
    [TestMethod]
    public async Task SendAsync_UsesTheWeightDeclaredOnTheRequest()
    {
        var clock = new FakeTimeProvider();
        var options = BuildOptions(limit: 10, window: TimeSpan.FromMinutes(1));
        using var limiter = new WeightedRateLimiter(options, clock);
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var client = new HttpClient(new RateLimitingHandler(limiter) { InnerHandler = stub });

        using var first = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        first.WithWeight(10);
        using var firstResponse = await client.SendAsync(first, CancellationToken.None);

        Assert.AreEqual(1, stub.CallCount);

        // 權重 10 已經把整個時間窗吃光,下一個請求必須等。
        // A weight of 10 consumes the whole window, so the next request has to wait.
        using var second = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        second.WithWeight(1);
        var pending = client.SendAsync(second, CancellationToken.None);

        Assert.IsFalse(pending.IsCompleted);
        using var secondResponse = await FakeClockRunner.RunAsync(clock, pending, TimeSpan.FromSeconds(10));

        Assert.AreEqual(2, stub.CallCount);
    }

    [TestMethod]
    public async Task SendAsync_RequestWithoutDeclaredWeight_UsesTheDefault()
    {
        var clock = new FakeTimeProvider();
        var options = BuildOptions(limit: 4, window: TimeSpan.FromMinutes(1));
        options.DefaultWeight = 2;
        using var limiter = new WeightedRateLimiter(options, clock);
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var client = new HttpClient(new RateLimitingHandler(limiter) { InnerHandler = stub });

        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
            using var response = await client.SendAsync(request, CancellationToken.None);
        }

        using var third = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var pending = client.SendAsync(third, CancellationToken.None);

        Assert.IsFalse(pending.IsCompleted, "預設權重 2 × 2 次已用完上限 4。Two requests at the default weight of 2 exhaust the limit of 4.");
        using var thirdResponse = await FakeClockRunner.RunAsync(clock, pending, TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task SendAsync_AcquisitionFails_ThrowsWithoutSendingTheRequest()
    {
        var clock = new FakeTimeProvider();
        var options = BuildOptions(limit: 1, window: TimeSpan.FromHours(1));
        options.AcquisitionTimeout = TimeSpan.FromSeconds(1);
        using var limiter = new WeightedRateLimiter(options, clock);
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var client = new HttpClient(new RateLimitingHandler(limiter) { InnerHandler = stub });

        using var first = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var firstResponse = await client.SendAsync(first, CancellationToken.None);

        using var second = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var exception = await Assert.ThrowsExactlyAsync<ResultException>(
            () => client.SendAsync(second, CancellationToken.None));

        Assert.AreEqual(HttpErrorCodes.RateLimitTimeout, exception.Error.Code);
        Assert.AreEqual(1, stub.CallCount, "排不到額度的請求不該離開本機。A request that cannot get permits must never leave the machine.");
    }

    [TestMethod]
    public void Constructor_OwningTheLimiter_DisposesItWithTheHandler()
    {
        var clock = new FakeTimeProvider();
        var handler = new RateLimitingHandler(BuildOptions(limit: 5, window: TimeSpan.FromMinutes(1)), clock)
        {
            InnerHandler = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK),
        };

        handler.Dispose();

        // 重複釋放不應拋例外。Disposing twice must not throw.
        handler.Dispose();
    }

    [TestMethod]
    public void Constructor_NullLimiter_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => new RateLimitingHandler((WeightedRateLimiter)null!));

    private static RateLimitOptions BuildOptions(int limit, TimeSpan window)
    {
        var options = new RateLimitOptions { AcquisitionTimeout = TimeSpan.FromHours(24) };
        options.Buckets.Add(new RateLimitBucket("test", limit, window));
        return options;
    }
}

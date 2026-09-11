using System.Net;
using System.Net.Http.Headers;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Tests.TestSupport;

namespace Ozakboy.Http.Tests;

/// <summary>
/// 重試行為的測試。退避等待一律以假時鐘推進,沒有任何真實等待。
/// Tests for retry behaviour. Backoff waits are driven by a fake clock, with no real waiting anywhere.
/// </summary>
[TestClass]
public sealed class RetryHandlerTests
{
    private static readonly RetryPolicy ThreeAttempts = new()
    {
        MaxAttempts = 3,
        BaseDelay = TimeSpan.FromMilliseconds(200),
        Strategy = BackoffStrategy.Exponential,
        JitterRatio = 0d,
    };

    [TestMethod]
    public async Task SendAsync_TransientStatus_IsRetriedUntilItSucceeds()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.ReturnsSequence(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);
        using var client = CreateClient(stub, clock, ThreeAttempts);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromMilliseconds(100));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, stub.CallCount);
    }

    [TestMethod]
    public async Task SendAsync_NonTransientStatus_IsNotRetried()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.BadRequest);
        using var client = CreateClient(stub, clock, ThreeAttempts);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(1, stub.CallCount, "400 重送只會得到同一個答案,不該重試。Resending a 400 earns the same answer; it must not be retried.");
    }

    [TestMethod]
    public async Task SendAsync_AttemptsExhausted_ReturnsTheLastResponse()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.ServiceUnavailable);
        using var client = CreateClient(stub, clock, ThreeAttempts);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromMilliseconds(100));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(3, stub.CallCount, "次數用盡就停,不該無限重試。It stops once the attempts run out rather than retrying forever.");
    }

    [TestMethod]
    public async Task SendAsync_PostByDefault_IsNeverRetried()
    {
        // 這是整個套件最重要的一條行為:下單請求逾時後不能自動重送,否則就是重複下單。
        // The single most important behaviour in this package: an order request that times out must not be
        // resent automatically, because that means placing the order twice.
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.ServiceUnavailable);
        using var client = CreateClient(stub, clock, ThreeAttempts);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/api/order")
        {
            Content = new StringContent("symbol=BTCUSDT"),
        };

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(1, stub.CallCount, "POST 預設不重試。POST is not retried by default.");
    }

    [TestMethod]
    public async Task SendAsync_DeleteByDefault_IsNeverRetried()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.ServiceUnavailable);
        using var client = CreateClient(stub, clock, ThreeAttempts);

        using var request = new HttpRequestMessage(HttpMethod.Delete, "https://example.test/api/order");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(1, stub.CallCount);
    }

    [TestMethod]
    public async Task SendAsync_PostExplicitlyMarkedIdempotent_IsRetried()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.ReturnsSequence(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var client = CreateClient(stub, clock, ThreeAttempts);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/api/query")
        {
            Content = new StringContent("body"),
        };
        request.AsIdempotent();

        using var response = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromMilliseconds(100));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, stub.CallCount);
        Assert.AreEqual("body", stub.Requests[1].Body, "重送的請求內容必須與原件相同。The resent request must carry the same body.");
    }

    [TestMethod]
    public async Task SendAsync_GetExplicitlyMarkedNonIdempotent_IsNotRetried()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.ServiceUnavailable);
        using var client = CreateClient(stub, clock, ThreeAttempts);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        request.AsNonIdempotent();

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(1, stub.CallCount, "明確宣告非冪等就該蓋過方法推定。An explicit declaration must override the method-based inference.");
    }

    [TestMethod]
    public async Task SendAsync_RetryAfterHeader_IsHonouredInsteadOfTheComputedBackoff()
    {
        var clock = new FakeTimeProvider();
        var stub = new StubHttpMessageHandler((_, attempt, _) =>
        {
            if (attempt > 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });

        using var client = CreateClient(stub, clock, ThreeAttempts);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");

        var pending = client.SendAsync(request, CancellationToken.None);

        // 自己算的退避間隔只有 200 毫秒。若沒有尊重 Retry-After,推進 1 秒就足以讓它重試完成。
        // The computed backoff is only 200 ms. Without honouring Retry-After, advancing one second would be
        // enough for the retry to complete.
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.IsFalse(pending.IsCompleted, "應該還在等 Retry-After 指定的 30 秒。It should still be waiting out the 30 seconds Retry-After asked for.");

        using var response = await FakeClockRunner.RunAsync(clock, pending, TimeSpan.FromSeconds(5));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, stub.CallCount);
    }

    [TestMethod]
    public async Task SendAsync_RetryAfterBeyondTheCeiling_IsCapped()
    {
        var clock = new FakeTimeProvider();
        var stub = new StubHttpMessageHandler((_, attempt, _) =>
        {
            if (attempt > 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(1));
            return Task.FromResult(response);
        });

        var options = new RetryOptions { Policy = ThreeAttempts, MaxRetryAfter = TimeSpan.FromSeconds(5) };
        using var client = CreateClient(stub, clock, options);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");

        using var response = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromSeconds(1));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task SendAsync_RetryAfterDisabled_UsesTheComputedBackoff()
    {
        var clock = new FakeTimeProvider();
        var stub = new StubHttpMessageHandler((_, attempt, _) =>
        {
            if (attempt > 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        });

        var options = new RetryOptions { Policy = ThreeAttempts, RespectRetryAfter = false };
        using var client = CreateClient(stub, clock, options);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");

        var pending = client.SendAsync(request, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await pending;

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task SendAsync_NetworkFailure_IsRetried()
    {
        var clock = new FakeTimeProvider();
        var stub = new StubHttpMessageHandler((_, attempt, _) => attempt == 1
            ? throw new HttpRequestException("連線被重設。Connection reset.")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        using var client = CreateClient(stub, clock, ThreeAttempts);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");

        using var response = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromMilliseconds(100));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, stub.CallCount);
    }

    [TestMethod]
    public async Task SendAsync_NetworkFailureOnANonRetryableRequest_Propagates()
    {
        var clock = new FakeTimeProvider();
        var stub = new StubHttpMessageHandler((_, _, _) => throw new HttpRequestException("連線失敗。Connection failed."));

        using var client = CreateClient(stub, clock, ThreeAttempts);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/api");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.SendAsync(request, CancellationToken.None));
        Assert.AreEqual(1, stub.CallCount);
    }

    [TestMethod]
    public async Task SendAsync_AttemptTimeout_SurfacesAsATimeoutError()
    {
        var clock = new FakeTimeProvider();
        var stub = new StubHttpMessageHandler(async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var timeouts = new HttpTimeoutOptions
        {
            AttemptTimeout = TimeSpan.FromSeconds(2),
            OverallTimeout = TimeSpan.FromMinutes(5),
        };

        using var client = new HttpClient(new RetryHandler(new RetryOptions { Policy = RetryPolicy.NoRetry }, timeouts, clock)
        {
            InnerHandler = stub,
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var pending = client.SendAsync(request, CancellationToken.None);

        var exception = await Assert.ThrowsExactlyAsync<HttpPipelineException>(
            () => FakeClockRunner.RunAsync(clock, pending, TimeSpan.FromSeconds(1)));

        Assert.AreEqual(HttpErrorCodes.AttemptTimeout, exception.Error.Code);
        Assert.AreEqual(ErrorCategory.Timeout, exception.Error.Category);
        Assert.IsTrue(exception.Error.IsTransient);
    }

    [TestMethod]
    public async Task SendAsync_PerRequestPolicyOverride_Wins()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.ServiceUnavailable);
        using var client = CreateClient(stub, clock, ThreeAttempts);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        request.WithRetryPolicy(RetryPolicy.NoRetry);

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(1, stub.CallCount);
    }

    [TestMethod]
    public async Task SendAsync_RetriedRequest_KeepsItsPerRequestOptions()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.ReturnsSequence(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var observed = new List<int?>();

        var recorder = new DelegateRecordingHandler(request => observed.Add(request.GetWeight()))
        {
            InnerHandler = stub,
        };

        using var client = new HttpClient(new RetryHandler(new RetryOptions { Policy = ThreeAttempts }, LongTimeouts(), clock)
        {
            InnerHandler = recorder,
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        request.WithWeight(7);

        using var response = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromMilliseconds(100));

        CollectionAssert.AreEqual(new int?[] { 7, 7 }, observed, "重送的副本必須保留每請求宣告。The resent copy must keep the per-request declarations.");
    }

    [TestMethod]
    public void IsRetryable_FollowsTheDeclarationThenTheMethod()
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        using var head = new HttpRequestMessage(HttpMethod.Head, "https://example.test/");
        using var post = new HttpRequestMessage(HttpMethod.Post, "https://example.test/");
        using var put = new HttpRequestMessage(HttpMethod.Put, "https://example.test/");

        Assert.IsTrue(RetryHandler.IsRetryable(get));
        Assert.IsTrue(RetryHandler.IsRetryable(head));
        Assert.IsFalse(RetryHandler.IsRetryable(post));
        Assert.IsFalse(RetryHandler.IsRetryable(put), "PUT 在 RFC 上是冪等的,但對方是否照規矩實作無從得知,預設仍不重試。PUT is idempotent per the RFC, but there is no way to know the peer implemented it that way, so it is not retried by default.");

        Assert.IsTrue(RetryHandler.IsRetryable(post.AsIdempotent()));
        Assert.IsFalse(RetryHandler.IsRetryable(get.AsNonIdempotent()));
    }

    [TestMethod]
    public void Constructor_InvalidOptions_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => new RetryHandler(new RetryOptions { MaxRetryAfter = TimeSpan.Zero }));

    [TestMethod]
    public void Constructor_InvalidTimeouts_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => new RetryHandler(
            new RetryOptions(),
            new HttpTimeoutOptions { AttemptTimeout = TimeSpan.FromMinutes(2), OverallTimeout = TimeSpan.FromSeconds(1) }));

    [TestMethod]
    public void Constructor_NullOptions_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => new RetryHandler((RetryOptions)null!));

    private static HttpTimeoutOptions LongTimeouts() => new()
    {
        AttemptTimeout = TimeSpan.FromMinutes(10),
        OverallTimeout = TimeSpan.FromHours(1),
    };

    private static HttpClient CreateClient(StubHttpMessageHandler stub, TimeProvider clock, RetryPolicy policy) =>
        CreateClient(stub, clock, new RetryOptions { Policy = policy });

    private static HttpClient CreateClient(StubHttpMessageHandler stub, TimeProvider clock, RetryOptions options) =>
        new(new RetryHandler(options, LongTimeouts(), clock) { InnerHandler = stub })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private sealed class DelegateRecordingHandler : DelegatingHandler
    {
        private readonly Action<HttpRequestMessage> _observer;

        public DelegateRecordingHandler(Action<HttpRequestMessage> observer) => _observer = observer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_observer)
            {
                _observer(request);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}

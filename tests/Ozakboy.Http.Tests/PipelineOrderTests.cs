using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Signing;
using Ozakboy.Http.Tests.TestSupport;

namespace Ozakboy.Http.Tests;

/// <summary>
/// 鎖住管線順序(重試 → 簽章 → 限流 → 日誌)帶來的行為:每一次嘗試都是一個新請求。
/// Pins the behaviour that the pipeline order (retry, signing, rate limiting, logging) exists for: every attempt
/// is a new request.
/// </summary>
/// <remarks>
/// 0.2.0 的順序是「簽章 → 限流 → 重試 → 日誌」,與它自己的註解相反,而錯的順序不會有任何錯誤訊息。
/// 這裡的前三條測試在那個順序下都會變紅。
/// 0.2.0 ran "signing, rate limiting, retry, logging", contradicting its own comments, and a wrong order raises no
/// error at all. The first three tests here all go red under that order.
/// </remarks>
[TestClass]
public sealed class PipelineOrderTests
{
    private const string ClientName = "order";
    private const string SecretKey = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";
    private const string ApiKey = "vmPUZE6mv9SD5VNHk4HlWFsOr6aKE2zvsw0MuIgwCIPy6utIco14y7Ju91duEh8A";

    [TestMethod]
    public async Task EveryRetry_PaysItsOwnWeight()
    {
        // 失敗兩次再成功,共三次嘗試、每次權重 3,上限 9 應該剛好用完。
        // Fails twice and then succeeds: three attempts at weight 3 should use up a limit of exactly 9.
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.ReturnsSequence(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);

        using var provider = BuildProvider(clock, stub, permitLimit: 9);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api/v3/account").WithWeight(3);
        using var response = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromMilliseconds(50));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(3, stub.CallCount);

        var limiter = provider.GetRequiredKeyedService<WeightedRateLimiter>(ClientName);
        var probe = limiter.AcquireAsync(1, CancellationToken.None);
        Assert.IsFalse(
            probe.IsCompleted,
            "三次嘗試各付 3,上限 9 應該已經用完;若還排得到,代表重試沒有付權重。Three attempts at 3 each should have used up the limit of 9; if a permit is still available, the retries paid no weight.");

        var probeResult = await FakeClockRunner.RunAsync(clock, probe, TimeSpan.FromSeconds(10));
        Assert.IsTrue(probeResult.IsSuccess);
    }

    [TestMethod]
    public async Task RetryWithoutEnoughWeight_WaitsForPermitsInsteadOfBypassingTheLimiter()
    {
        // 上限 6、每次權重 3:第三次嘗試必須等下一個時間窗(1 分鐘)才能送出。
        // A limit of 6 at weight 3 per attempt: the third attempt has to wait for the next one-minute window.
        var clock = new FakeTimeProvider();
        var sentAt = new List<DateTimeOffset>();
        var stub = new StubHttpMessageHandler((_, attempt, _) =>
        {
            lock (sentAt)
            {
                sentAt.Add(clock.GetUtcNow());
            }

            return Task.FromResult(new HttpResponseMessage(attempt < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        });

        using var provider = BuildProvider(clock, stub, permitLimit: 6);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        var start = clock.GetUtcNow();

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api/v3/account").WithWeight(3);
        using var response = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromSeconds(1));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(3, sentAt.Count);
        Assert.IsTrue(sentAt[1] - start < TimeSpan.FromMinutes(1), "第二次嘗試還在配額內,不需要等。The second attempt is within quota and should not wait.");
        Assert.IsTrue(
            sentAt[2] - start >= TimeSpan.FromMinutes(1),
            $"第三次嘗試必須等到配額補充才送出,實際在 {sentAt[2] - start} 就送出了。The third attempt must wait for the quota to refill; it went out after {sentAt[2] - start}.");
    }

    [TestMethod]
    public async Task EveryRetry_IsSignedAfreshWithANewTimestamp()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.ReturnsSequence(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);

        using var provider = BuildProvider(clock, stub, permitLimit: 100);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api/v3/account")
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("symbol", "BTCUSDT").Add("timestamp", 1L).Build())
            .WithSignature();

        using var response = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromMilliseconds(50));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(3, stub.Requests.Count);

        var timestamps = new List<long>();
        foreach (var sent in stub.Requests)
        {
            var query = sent.RequestUri!.Query.TrimStart('?');
            var separator = query.IndexOf("&signature=", StringComparison.Ordinal);
            Assert.IsGreaterThan(0, separator, $"送出的請求缺少簽章:{query}。The request went out unsigned: {query}.");

            var canonical = query[..separator];
            var signature = query[(separator + "&signature=".Length)..];

            // 時間戳在原位被換掉,不是另外附加一個:參數順序就是簽章順序。
            // The timestamp is replaced in place rather than appended: parameter order is signing order.
            StringAssert.StartsWith(canonical, "symbol=BTCUSDT&timestamp=", StringComparison.Ordinal);
            var timestamp = long.Parse(canonical["symbol=BTCUSDT&timestamp=".Length..], CultureInfo.InvariantCulture);
            Assert.AreNotEqual(1L, timestamp, "呼叫端放的佔位時間戳必須被當下時間取代。The caller's placeholder timestamp must be replaced by the current time.");
            timestamps.Add(timestamp);

            Assert.IsTrue(HmacSha256SignatureAlgorithm.Instance.Sign(canonical, SecretKey).TryGetValue(out var expected));
            Assert.AreEqual(expected, signature, "每一次嘗試的簽章都必須對得上它自己送出的字串。Each attempt's signature must match the string it actually sent.");
            Assert.AreEqual(ApiKey, sent.Headers["X-API-Key"]);
        }

        Assert.AreEqual(
            3,
            timestamps.Distinct().Count(),
            $"三次嘗試的時間戳必須各不相同,實際為 {string.Join(", ", timestamps)}。The three attempts must carry distinct timestamps.");
    }

    [TestMethod]
    public async Task RetryBackoff_HoldsNoRateLimitPermit()
    {
        // 甲請求被 Retry-After 要求等 30 秒;這段期間乙請求必須照常通過限流,不必等甲。
        // Request A is told by Retry-After to wait 30 seconds; meanwhile request B must pass the limiter as usual.
        var clock = new FakeTimeProvider();
        var throttleUntil = clock.GetUtcNow() + TimeSpan.FromSeconds(1);
        var stub = new StubHttpMessageHandler((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/slow", StringComparison.Ordinal) && clock.GetUtcNow() < throttleUntil)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return Task.FromResult(throttled);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var provider = BuildProvider(clock, stub, permitLimit: 2);
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var clientA = factory.CreateClient(ClientName);
        using var clientB = factory.CreateClient(ClientName);

        using var slow = new HttpRequestMessage(HttpMethod.Get, "https://example.test/slow").WithWeight(1);
        using var fast = new HttpRequestMessage(HttpMethod.Get, "https://example.test/fast").WithWeight(1);

        var pendingSlow = clientA.SendAsync(slow, CancellationToken.None);
        var pendingFast = clientB.SendAsync(fast, CancellationToken.None);

        var finished = await Task.WhenAny(pendingFast, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.AreSame(pendingFast, finished, "甲在退避期間不該擋住乙。A's backoff must not block B.");
        Assert.IsFalse(pendingSlow.IsCompleted, "甲應該還在等 Retry-After。A should still be waiting out Retry-After.");

        using var fastResponse = await pendingFast;
        Assert.AreEqual(HttpStatusCode.OK, fastResponse.StatusCode);

        using var slowResponse = await FakeClockRunner.RunAsync(clock, pendingSlow, TimeSpan.FromSeconds(5));
        Assert.AreEqual(HttpStatusCode.OK, slowResponse.StatusCode);
    }

    [TestMethod]
    public async Task NonIdempotentRequest_IsStillNotRetried()
    {
        // 順序調整不可以改變這一條:下單請求失敗後自動重送,就是重複下單。
        // The reordering must not touch this: an order request resent automatically after a failure is a
        // duplicated order.
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.ServiceUnavailable);
        using var provider = BuildProvider(clock, stub, permitLimit: 100);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        using var post = new HttpRequestMessage(HttpMethod.Post, "https://example.test/fapi/v1/order")
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("symbol", "BTCUSDT").Build())
            .WithSignature();
        using var postResponse = await client.SendAsync(post, CancellationToken.None);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "https://example.test/fapi/v1/order").WithSignature();
        using var deleteResponse = await client.SendAsync(delete, CancellationToken.None);

        using var get = new HttpRequestMessage(HttpMethod.Get, "https://example.test/fapi/v1/order").AsNonIdempotent();
        using var getResponse = await client.SendAsync(get, CancellationToken.None);

        Assert.AreEqual(3, stub.CallCount, "三個不冪等的請求各只送一次。Each of the three non-idempotent requests goes out once.");
    }

    [TestMethod]
    public async Task NonIdempotentTransportFailure_IsStillNotRetried()
    {
        var clock = new FakeTimeProvider();
        var stub = new StubHttpMessageHandler((_, _, _) => throw new HttpRequestException("連線中斷。Connection dropped."));
        using var provider = BuildProvider(clock, stub, permitLimit: 100);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        using var post = new HttpRequestMessage(HttpMethod.Post, "https://example.test/fapi/v1/order").WithSignature();

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.SendAsync(post, CancellationToken.None));
        Assert.AreEqual(1, stub.CallCount);
    }

    [TestMethod]
    public async Task AttemptTimeoutWhileWaitingForPermits_IsReportedAsAnAttemptTimeout()
    {
        // 限流在重試之內,單次嘗試逾時也涵蓋等待許可的時間。那時限流器看到的是取消,
        // 但呼叫端什麼都沒取消 —— 回報必須是單次嘗試逾時(暫時性),不是呼叫端取消。
        // Rate limiting sits inside retry, so the attempt timeout covers the wait for permits too. The limiter
        // sees a cancellation, but the caller cancelled nothing: the report must be an attempt timeout
        // (transient), not a caller cancellation.
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var provider = BuildProvider(clock, stub, permitLimit: 3, extra: options =>
        {
            options.Retry.Policy = RetryPolicy.NoRetry;
            options.Timeouts.AttemptTimeout = TimeSpan.FromSeconds(2);
        });

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        using var first = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithWeight(3);
        using var firstResponse = await client.SendAsync(first, CancellationToken.None);

        using var second = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithWeight(1);
        var exception = await Assert.ThrowsExactlyAsync<ResultException>(() => FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(second, CancellationToken.None),
            TimeSpan.FromMilliseconds(500)));

        Assert.AreEqual(HttpErrorCodes.AttemptTimeout, exception.Error.Code);
        Assert.AreEqual(ErrorCategory.Timeout, exception.Error.Category);
        Assert.AreEqual(1, stub.CallCount, "第二個請求從未離開本機。The second request never left the machine.");
    }

    [TestMethod]
    public async Task SigningHandler_TimestampParameterAbsent_IsAppended()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123));
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var invoker = new HttpMessageInvoker(new SigningHandler(
            new SigningOptions { SecretKey = SecretKey, TimestampParameterName = "timestamp" },
            clock)
        {
            InnerHandler = stub,
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api")
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("symbol", "BTCUSDT").Build())
            .WithSignature();
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        StringAssert.StartsWith(
            stub.Requests[0].RequestUri!.Query,
            "?symbol=BTCUSDT&timestamp=1700000000123&signature=",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SigningHandler_UnsignedRequest_KeepsItsTimestampUntouched()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123));
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var invoker = new HttpMessageInvoker(new SigningHandler(
            new SigningOptions { SecretKey = SecretKey, TimestampParameterName = "timestamp" },
            clock)
        {
            InnerHandler = stub,
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api")
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("timestamp", 42L).Build());
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.AreEqual("?timestamp=42", stub.Requests[0].RequestUri!.Query);
    }

    [TestMethod]
    public void SigningOptions_BlankTimestampName_IsInvalid() =>
        Assert.IsTrue(new SigningOptions { TimestampParameterName = " " }.Validate().IsFailure);

    private static ServiceProvider BuildProvider(
        FakeTimeProvider clock,
        StubHttpMessageHandler stub,
        int permitLimit,
        Action<HttpPipelineOptions>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub)
            .AddOzakboyHttpPipeline(options =>
            {
                options.Signing.ApiKey = ApiKey;
                options.Signing.SecretKey = SecretKey;
                options.Signing.TimestampParameterName = "timestamp";
                options.RateLimiting.AcquisitionTimeout = TimeSpan.FromHours(1);
                options.RateLimiting.Buckets.Add(new RateLimitBucket("minute", permitLimit, TimeSpan.FromMinutes(1)));
                options.Retry.Policy = new RetryPolicy
                {
                    MaxAttempts = 3,
                    BaseDelay = TimeSpan.FromMilliseconds(100),
                    Strategy = BackoffStrategy.Exponential,
                    JitterRatio = 0d,
                };
                options.Timeouts.AttemptTimeout = TimeSpan.FromHours(1);
                options.Timeouts.OverallTimeout = TimeSpan.FromHours(2);
                extra?.Invoke(options);
            });

        return services.BuildServiceProvider();
    }
}

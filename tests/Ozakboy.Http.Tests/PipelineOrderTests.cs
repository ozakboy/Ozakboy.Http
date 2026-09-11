using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;
using Ozakboy.Http.Tests.TestSupport;

namespace Ozakboy.Http.Tests;

/// <summary>
/// 鎖住管線順序(重試 → 限流 → 簽章 → 日誌)帶來的行為:每一次嘗試都是一個新請求,時間戳是送出那一刻的。
/// Pins the behaviour the pipeline order (retry, rate limiting, signing, logging) exists for: every attempt is a
/// new request, and a timestamp is the moment the request goes out.
/// </summary>
/// <remarks>
/// 錯的順序不會有任何錯誤訊息。0.2.0 的順序(簽章 → 限流 → 重試)讓前三條測試變紅;
/// 0.3.0 開發中的順序(重試 → 簽章 → 限流)讓時間戳必須晚於放行時刻的那一條變紅。
/// A wrong order raises no error at all. The 0.2.0 order (signing, rate limiting, retry) turns the first three
/// tests red; the 0.3.0 draft order (retry, signing, rate limiting) turns the timestamp-after-release test red.
/// </remarks>
[TestClass]
public sealed class PipelineOrderTests
{
    private const string ClientName = "order";
    private const string SecretKey = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";
    private const string ApiKey = "vmPUZE6mv9SD5VNHk4HlWFsOr6aKE2zvsw0MuIgwCIPy6utIco14y7Ju91duEh8A";

    private static readonly RetryPolicy ThreeAttempts = new()
    {
        MaxAttempts = 3,
        BaseDelay = TimeSpan.FromMilliseconds(100),
        Strategy = BackoffStrategy.Exponential,
        JitterRatio = 0d,
    };

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
            var (canonical, signature) = SplitSigned(sent.RequestUri!);

            // 時間戳在原位被換掉,不是另外附加一個:參數順序就是簽章順序。
            // The timestamp is replaced in place rather than appended: parameter order is signing order.
            StringAssert.StartsWith(canonical, "symbol=BTCUSDT&timestamp=", StringComparison.Ordinal);
            var timestamp = ReadTimestamp(sent.RequestUri!);
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
    public async Task RequestHeldByTheLimiter_IsStampedAfterItIsReleased()
    {
        // 先簽章再排隊,時間戳就在隊伍裡過期(限流等待上限預設 30 秒,幣安 recvWindow 預設 5 秒)。
        // 第二個請求要等到一分鐘後的下一個時間窗才放行,它的時間戳必須晚於放行時刻,而不是它開始排隊的時刻。
        // Signed before queueing, a timestamp ages in the queue (the limiter waits up to 30 seconds by default,
        // Binance's recvWindow defaults to 5). The second request is released only at the next window a minute
        // later, and its timestamp must come after that release, not from when it started queueing.
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var provider = BuildProvider(clock, stub, permitLimit: 3, extra: options => options.Retry.Policy = RetryPolicy.NoRetry);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        var releasedAt = clock.GetUtcNow() + TimeSpan.FromMinutes(1);

        using var first = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithWeight(3);
        using var firstResponse = await client.SendAsync(first, CancellationToken.None);

        using var second = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api/v3/account")
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("symbol", "BTCUSDT").Add("timestamp", 1L).Build())
            .WithSignature()
            .WithWeight(1);

        var pending = client.SendAsync(second, CancellationToken.None);
        Assert.IsFalse(pending.IsCompleted, "配額已用完,第二個請求應該在排隊。The quota is spent, so the second request should be queueing.");

        using var response = await FakeClockRunner.RunAsync(clock, pending, TimeSpan.FromSeconds(1));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var sentUri = stub.Requests[1].RequestUri!;
        var timestamp = ReadTimestamp(sentUri);
        Assert.IsTrue(
            timestamp >= releasedAt.ToUnixTimeMilliseconds(),
            $"時間戳 {timestamp} 早於放行時刻 {releasedAt.ToUnixTimeMilliseconds()}:請求是在排隊前簽的。The timestamp {timestamp} predates the release at {releasedAt.ToUnixTimeMilliseconds()}: the request was signed before it queued.");

        var (canonical, signature) = SplitSigned(sentUri);
        Assert.IsTrue(HmacSha256SignatureAlgorithm.Instance.Sign(canonical, SecretKey).TryGetValue(out var expected));
        Assert.AreEqual(expected, signature);
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
    public async Task LimiterWait_DoesNotCountTowardsTheAttemptTimeout()
    {
        // 單次嘗試逾時 2 秒,但請求要在限流器前排一分鐘。排隊不算嘗試時間,請求最後照常送出、成功。
        // The attempt timeout is 2 seconds but the request queues at the limiter for a minute. Queueing is not
        // attempt time, so the request eventually goes out and succeeds.
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var provider = BuildProvider(clock, stub, permitLimit: 3, extra: options =>
        {
            options.Retry.Policy = RetryPolicy.NoRetry;
            options.Timeouts.AttemptTimeout = TimeSpan.FromSeconds(2);
        });

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        var start = clock.GetUtcNow();

        using var first = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithWeight(3);
        using var firstResponse = await client.SendAsync(first, CancellationToken.None);

        using var second = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithWeight(1);
        using var secondResponse = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(second, CancellationToken.None),
            TimeSpan.FromMilliseconds(500));

        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.AreEqual(2, stub.CallCount);
        Assert.IsTrue(clock.GetUtcNow() - start >= TimeSpan.FromMinutes(1), "第二個請求確實排了一分鐘的隊。The second request really did queue for a minute.");
    }

    [TestMethod]
    public async Task AttemptTimeout_StartsOnceThePermitIsGranted()
    {
        // 放行之後對方不回應:單次逾時從放行那一刻起算,2 秒後觸發。
        // The peer never answers after the release: the attempt timeout runs from the moment of release and
        // fires 2 seconds later.
        var clock = new FakeTimeProvider();
        DateTimeOffset? sentAt = null;
        var stub = new StubHttpMessageHandler(async (_, attempt, token) =>
        {
            if (attempt == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            sentAt = clock.GetUtcNow();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var provider = BuildProvider(clock, stub, permitLimit: 3, extra: options =>
        {
            options.Retry.Policy = RetryPolicy.NoRetry;
            options.Timeouts.AttemptTimeout = TimeSpan.FromSeconds(2);
        });

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        var start = clock.GetUtcNow();

        using var first = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithWeight(3);
        using var firstResponse = await client.SendAsync(first, CancellationToken.None);

        using var second = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithWeight(1);
        var exception = await Assert.ThrowsExactlyAsync<ResultException>(() => FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(second, CancellationToken.None),
            TimeSpan.FromMilliseconds(500)));
        var failedAt = clock.GetUtcNow();

        Assert.AreEqual(HttpErrorCodes.AttemptTimeout, exception.Error.Code);
        Assert.IsNotNull(sentAt, "請求必須在放行後送出才逾時。The request must have gone out before timing out.");
        Assert.IsTrue(sentAt.Value - start >= TimeSpan.FromMinutes(1), "請求先排了一分鐘的隊。The request queued for a minute first.");

        var measured = failedAt - sentAt.Value;
        Assert.IsTrue(
            measured >= TimeSpan.FromSeconds(2) && measured < TimeSpan.FromSeconds(3),
            $"逾時必須從放行起算 2 秒,實際為 {measured}。The timeout must run 2 seconds from the release; it ran {measured}.");
    }

    [TestMethod]
    public async Task OverallTimeout_StillCoversTheLimiterWait()
    {
        // 排隊不算單次嘗試,但整體逾時仍涵蓋全部;排隊中觸發要回報為整體逾時,不是呼叫端取消。
        // Queueing is not attempt time, but the overall timeout still covers everything; firing during the queue
        // is reported as the overall timeout, not a caller cancellation.
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var provider = BuildProvider(clock, stub, permitLimit: 3, extra: options =>
        {
            options.Retry.Policy = RetryPolicy.NoRetry;
            options.Timeouts.AttemptTimeout = TimeSpan.FromSeconds(2);
            options.Timeouts.OverallTimeout = TimeSpan.FromSeconds(10);
        });

        var pipeline = provider.CreateOzakboyHttpPipelineClient(ClientName);

        using var first = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithWeight(3);
        var firstResult = await pipeline.SendAsync(first, CancellationToken.None);
        Assert.IsTrue(firstResult.TryGetValue(out var firstResponse));
        firstResponse.Dispose();

        using var second = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithWeight(1);
        var result = await FakeClockRunner.RunAsync(clock, pipeline.SendAsync(second, CancellationToken.None), TimeSpan.FromSeconds(1));

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(HttpErrorCodes.Timeout, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Timeout, result.Error!.Category);
        Assert.AreEqual(1, stub.CallCount, "第二個請求從未離開本機。The second request never left the machine.");
    }

    [TestMethod]
    public async Task LocalRateLimitTimeout_IsNotRetriedByTheDefaultPolicy()
    {
        // 限流器已經等滿了上限;重試只會把同樣長的等待乘上嘗試次數。
        // The limiter has already waited out its ceiling; a retry only multiplies that wait by the attempt count.
        var clock = new FakeTimeProvider();
        var stub = new StubHttpMessageHandler((_, _, _) =>
            throw Error.RateLimited(HttpErrorCodes.RateLimitTimeout, "排不到額度。No permits.").ToException());

        using var client = CreateRetryClient(stub, clock, ThreeAttempts);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");

        var exception = await Assert.ThrowsExactlyAsync<ResultException>(() => client.SendAsync(request, CancellationToken.None));

        Assert.AreEqual(1, stub.CallCount, "本地限流逾時不重試。A local rate-limit timeout is not retried.");
        Assert.AreEqual(HttpErrorCodes.RateLimitTimeout, exception.Error.Code);
        Assert.AreEqual(ErrorCategory.RateLimited, exception.Error.Category, "沒有重試過,就不是「已用盡」。Nothing was retried, so nothing was exhausted.");
    }

    [TestMethod]
    public async Task LocalRateLimitTimeout_IsRetriedWhenThePredicateSaysSo()
    {
        // 設了判斷式就完全由它決定,與策略「取代而非附加」的語意一致。
        // A predicate decides entirely for itself, in keeping with the policy's "replace, not add" semantics.
        var clock = new FakeTimeProvider();
        var stub = new StubHttpMessageHandler((_, _, _) =>
            throw Error.RateLimited(HttpErrorCodes.RateLimitTimeout, "排不到額度。No permits.").ToException());

        using var client = CreateRetryClient(stub, clock, ThreeAttempts with { RetryPredicate = error => error.IsTransient });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");

        var exception = await Assert.ThrowsExactlyAsync<ResultException>(() => FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromMilliseconds(50)));

        Assert.AreEqual(3, stub.CallCount);
        Assert.AreEqual(ErrorCategory.Exhausted, exception.Error.Category);
    }

    [TestMethod]
    public async Task LocalRateLimitTimeout_ThroughThePipeline_IsNotRetried()
    {
        // 限流器排不到(下一個時間窗超過 5 秒的等待上限)就直接失敗;預設策略下只排這一次,不會重試到「已用盡」。
        // The limiter gives up when the next window lies beyond its 5-second ceiling; under the default policy
        // that happens once, not retried until "exhausted".
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var provider = BuildProvider(clock, stub, permitLimit: 3, extra: options =>
            options.RateLimiting.AcquisitionTimeout = TimeSpan.FromSeconds(5));
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        using var first = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithWeight(3);
        using var firstResponse = await client.SendAsync(first, CancellationToken.None);

        using var second = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithWeight(1);
        var exception = await Assert.ThrowsExactlyAsync<ResultException>(() => FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(second, CancellationToken.None),
            TimeSpan.FromMilliseconds(50)));

        Assert.AreEqual(HttpErrorCodes.RateLimitTimeout, exception.Error.Code);
        Assert.AreEqual(ErrorCategory.RateLimited, exception.Error.Category);
        Assert.AreEqual(1, stub.CallCount);
    }

    [TestMethod]
    public void CreateOzakboyHttpPipelineClient_RequiresThePipelineRegistration()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("logging-only").AddSanitizedLogging();
        using var provider = services.BuildServiceProvider();

        Assert.ThrowsExactly<InvalidOperationException>(() => provider.CreateOzakboyHttpPipelineClient("logging-only"));
        Assert.ThrowsExactly<InvalidOperationException>(() => provider.CreateOzakboyHttpPipelineClient("nobody"));
        Assert.ThrowsExactly<ArgumentNullException>(() => OzakboyHttpServiceProviderExtensions.CreateOzakboyHttpPipelineClient(null!, "x"));
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

    private static (string Canonical, string Signature) SplitSigned(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        var separator = query.IndexOf("&signature=", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, separator, $"送出的請求缺少簽章:{query}。The request went out unsigned: {query}.");
        return (query[..separator], query[(separator + "&signature=".Length)..]);
    }

    private static long ReadTimestamp(Uri uri)
    {
        var pair = uri.Query.TrimStart('?').Split('&').Single(part => part.StartsWith("timestamp=", StringComparison.Ordinal));
        return long.Parse(pair["timestamp=".Length..], CultureInfo.InvariantCulture);
    }

    private static HttpClient CreateRetryClient(StubHttpMessageHandler stub, TimeProvider clock, RetryPolicy policy) =>
        new(new RetryHandler(
            new RetryOptions { Policy = policy },
            new HttpTimeoutOptions { AttemptTimeout = TimeSpan.FromMinutes(10), OverallTimeout = TimeSpan.FromHours(1) },
            clock)
        {
            InnerHandler = stub,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

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
                options.Retry.Policy = ThreeAttempts;
                options.Timeouts.AttemptTimeout = TimeSpan.FromHours(1);
                options.Timeouts.OverallTimeout = TimeSpan.FromHours(2);
                extra?.Invoke(options);
            });

        return services.BuildServiceProvider();
    }
}

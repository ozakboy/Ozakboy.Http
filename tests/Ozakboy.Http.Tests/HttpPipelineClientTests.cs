using System.Net;
using System.Net.Http.Headers;
using Ozakboy.Http.Tests.TestSupport;

namespace Ozakboy.Http.Tests;

[TestClass]
public sealed class HttpPipelineClientTests
{
    [TestMethod]
    public async Task SendAsync_SuccessfulExchange_ReturnsTheResponse()
    {
        using var httpClient = new HttpClient(StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK, "pong"));
        var client = new HttpPipelineClient(httpClient, LongTimeouts());

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/ping");
        var result = await client.SendAsync(request, CancellationToken.None);

        Assert.IsTrue(result.TryGetValue(out var response));
        using (response)
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [TestMethod]
    public async Task SendAsync_NonSuccessStatus_IsStillASuccessfulResult()
    {
        // 狀態碼怎麼解讀是呼叫端的事:同一個 400 在不同服務代表的意義差很多。
        // What a status code means is the caller's business: the same 400 means very different things to
        // different services.
        using var httpClient = new HttpClient(StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.BadRequest));
        var client = new HttpPipelineClient(httpClient, LongTimeouts());

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var result = await client.SendAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        result.GetValueOrDefault()?.Dispose();
    }

    [TestMethod]
    public async Task SendAsync_PipelineFailure_BecomesAFailedResult()
    {
        var stub = new StubHttpMessageHandler((_, _, _) =>
            throw Error.RateLimited(HttpErrorCodes.RateLimitTimeout, "排不到額度。No permits.").ToException());

        using var httpClient = new HttpClient(stub);
        var client = new HttpPipelineClient(httpClient, LongTimeouts());

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var result = await client.SendAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(HttpErrorCodes.RateLimitTimeout, result.Error!.Code);
    }

    [TestMethod]
    public async Task SendAsync_NetworkFailure_BecomesATransientFailedResult()
    {
        var stub = new StubHttpMessageHandler((_, _, _) => throw new HttpRequestException("down"));
        using var httpClient = new HttpClient(stub);
        var client = new HttpPipelineClient(httpClient, LongTimeouts());

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var result = await client.SendAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(ErrorCategory.Network, result.Error!.Category);
        Assert.IsTrue(result.Error!.IsTransient);
    }

    [TestMethod]
    public async Task SendAsync_OverallTimeout_ReportsTimeoutRatherThanCancellation()
    {
        var clock = new FakeTimeProvider();
        var stub = new StubHttpMessageHandler(async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = new HttpClient(stub) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new HttpPipelineClient(
            httpClient,
            new HttpTimeoutOptions { AttemptTimeout = TimeSpan.FromSeconds(1), OverallTimeout = TimeSpan.FromSeconds(3) },
            clock);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var result = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromSeconds(1));

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(HttpErrorCodes.Timeout, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Timeout, result.Error!.Category);
    }

    [TestMethod]
    public async Task SendAsync_CallerCancellation_ReportsCancelledRatherThanTimeout()
    {
        var stub = new StubHttpMessageHandler(async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = new HttpClient(stub) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new HttpPipelineClient(httpClient, LongTimeouts());
        using var cancellation = new CancellationTokenSource();

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var pending = client.SendAsync(request, cancellation.Token);
        await cancellation.CancelAsync();
        var result = await pending;

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(HttpErrorCodes.Cancelled, result.Error!.Code);
        Assert.IsFalse(result.Error!.IsTransient, "使用者按下取消不是暫時性故障,不該被重試或告警。A user cancelling is not a transient fault and deserves neither a retry nor an alert.");
    }

    [TestMethod]
    public async Task SendForStringAsync_SuccessfulResponse_ReturnsTheBody()
    {
        using var httpClient = new HttpClient(StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK, "{\"ok\":true}"));
        var client = new HttpPipelineClient(httpClient, LongTimeouts());

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var result = await client.SendForStringAsync(request, CancellationToken.None);

        Assert.IsTrue(result.TryGetValue(out var body));
        Assert.AreEqual("{\"ok\":true}", body);
    }

    [TestMethod]
    public async Task SendForStringAsync_NonSuccessStatus_IsAFailureCarryingTheCategory()
    {
        using var httpClient = new HttpClient(StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.TooManyRequests, "slow down"));
        var client = new HttpPipelineClient(httpClient, LongTimeouts());

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var result = await client.SendForStringAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(ErrorCategory.RateLimited, result.Error!.Category);
        Assert.IsTrue(result.Error!.IsTransient);
    }

    [TestMethod]
    public async Task SendForStringAsync_RateLimited_CarriesTheStatusAndRetryAfterIntoTheError()
    {
        // 回應在這個方法裡就被釋放了,呼叫端只拿得到 Result。狀態碼與 Retry-After 若沒在這時候
        // 寫進錯誤,之後就再也讀不到 —— 而這兩個正是下游最需要用程式判斷的數值。
        // The response is disposed inside this method and the caller only ever sees a Result. If the status
        // and Retry-After are not written into the error here they are gone for good — and those two are
        // exactly what downstream code needs to branch on.
        var stub = new StubHttpMessageHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("slow down"),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });

        using var httpClient = new HttpClient(stub);
        var client = new HttpPipelineClient(httpClient, LongTimeouts());

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var result = await client.SendForStringAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error!.TryGetInt64(HttpErrorDataKeys.StatusCode, out var statusCode));
        Assert.AreEqual(429L, statusCode);
        Assert.IsTrue(result.Error!.TryGetDecimal(HttpErrorDataKeys.RetryAfterSeconds, out var seconds));
        Assert.AreEqual(30m, seconds);
    }

    [TestMethod]
    public async Task SendForStringAsync_TransportFailure_ForwardsTheFailure()
    {
        var stub = new StubHttpMessageHandler((_, _, _) => throw new HttpRequestException("down"));
        using var httpClient = new HttpClient(stub);
        var client = new HttpPipelineClient(httpClient, LongTimeouts());

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var result = await client.SendForStringAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(ErrorCategory.Network, result.Error!.Category);
    }

    [TestMethod]
    public async Task SendAsync_BuiltFromAFactory_TakesAClientForEveryRequest()
    {
        // 門面會被註冊成單例(下游正是這樣用的),所以它絕不能在建構時取一個 HttpClient 拿著不放:
        // IHttpClientFactory 的處理器輪替(SetHandlerLifetime,預設兩分鐘)只在每次 CreateClient 時才有機會發生,
        // 長期持有同一個用戶端等於永遠綁在同一組連線上,對方換 IP 之後 DNS 跟不上 —— 無人值守跑一整天,這是真問題。
        // 這一條直接數 CreateClient 的次數:三個請求就該是三次,不是一次。
        // The facade gets registered as a singleton (that is exactly how downstream uses it), so it must never
        // take one HttpClient at construction and hold on to it: IHttpClientFactory's handler rotation
        // (SetHandlerLifetime, two minutes by default) only gets its chance on each CreateClient call, and
        // holding one client pins the pipeline to one set of connections, so DNS cannot keep up once the peer
        // moves to a new IP — a real problem on an unattended day-long run. This test simply counts the
        // CreateClient calls: three requests must be three calls, not one.
        using var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK, "pong");
        var factory = new CountingHttpClientFactory(stub);
        var client = new HttpPipelineClient(factory, "exchange", LongTimeouts());

        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/ping");
            var result = await client.SendAsync(request, CancellationToken.None);

            Assert.IsTrue(result.TryGetValue(out var response));
            response.Dispose();
        }

        Assert.AreEqual(3, stub.CallCount);
        Assert.AreEqual(
            3,
            factory.CreateClientCount,
            "每一次請求都要向工廠取一次用戶端;只取一次代表門面長期持有同一個 HttpClient,處理器永遠不會輪替。A client must be taken from the factory per request; a single call means the facade is holding one HttpClient for good and the handlers will never rotate.");
        Assert.AreEqual(3, factory.RequestedNames.Count);
        Assert.IsTrue(
            factory.RequestedNames.TrueForAll(name => string.Equals(name, "exchange", StringComparison.Ordinal)),
            "每次都要取同一個具名用戶端。The same named client must be requested every time.");
    }

    [TestMethod]
    public async Task SendForStringAsync_BuiltFromAFactory_TakesAClientForEveryRequest()
    {
        // SendForStringAsync 走的是同一條送出路徑,一併鎖住,以免哪天它改成自己送。
        // SendForStringAsync goes out through the same path; pinned here too, in case it ever starts sending on
        // its own.
        using var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK, "pong");
        var factory = new CountingHttpClientFactory(stub);
        var client = new HttpPipelineClient(factory, "exchange", LongTimeouts());

        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/ping");
            var result = await client.SendForStringAsync(request, CancellationToken.None);

            Assert.IsTrue(result.TryGetValue(out var body));
            Assert.AreEqual("pong", body);
        }

        Assert.AreEqual(3, factory.CreateClientCount);
    }

    [TestMethod]
    public async Task SendAsync_BuiltFromAnHttpClient_KeepsUsingTheClientItWasGiven()
    {
        // 手動建立的那條路徑仍在(Telegram 套件用它):門面用呼叫端給的那一個,生命週期也由呼叫端負責。
        // The hand-built path is still there (the Telegram package uses it): the facade uses the client it was
        // handed, and that client's lifetime stays with the caller.
        using var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK, "pong");
        using var httpClient = new HttpClient(stub, disposeHandler: false);
        var client = new HttpPipelineClient(httpClient, LongTimeouts());

        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/ping");
            var result = await client.SendAsync(request, CancellationToken.None);

            Assert.IsTrue(result.TryGetValue(out var response));
            response.Dispose();
        }

        Assert.AreEqual(3, stub.CallCount);
    }

    [TestMethod]
    public void Constructor_NullClient_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => new HttpPipelineClient(null!));

    [TestMethod]
    public void Constructor_NullFactory_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => new HttpPipelineClient((IHttpClientFactory)null!, "exchange"));

    [TestMethod]
    public void Constructor_BlankClientName_Throws()
    {
        using var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        var factory = new CountingHttpClientFactory(stub);

        Assert.ThrowsExactly<ArgumentNullException>(() => new HttpPipelineClient(factory, null!));
        Assert.ThrowsExactly<ArgumentException>(() => new HttpPipelineClient(factory, "   "));
    }

    [TestMethod]
    public void Constructor_FactoryOverload_ValidatesTheTimeouts()
    {
        using var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        var factory = new CountingHttpClientFactory(stub);

        Assert.ThrowsExactly<ArgumentException>(() => new HttpPipelineClient(
            factory,
            "exchange",
            new HttpTimeoutOptions { AttemptTimeout = TimeSpan.FromMinutes(1), OverallTimeout = TimeSpan.FromSeconds(1) }));
    }

    [TestMethod]
    public void Constructor_OverallShorterThanAttempt_Throws()
    {
        using var httpClient = new HttpClient(StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK));

        Assert.ThrowsExactly<ArgumentException>(() => new HttpPipelineClient(
            httpClient,
            new HttpTimeoutOptions { AttemptTimeout = TimeSpan.FromMinutes(1), OverallTimeout = TimeSpan.FromSeconds(1) }));
    }

    private static HttpTimeoutOptions LongTimeouts() => new()
    {
        AttemptTimeout = TimeSpan.FromMinutes(10),
        OverallTimeout = TimeSpan.FromHours(1),
    };
}

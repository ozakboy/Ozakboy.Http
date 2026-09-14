using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;
using Ozakboy.Http.Tests.TestSupport;

namespace Ozakboy.Http.Tests;

/// <summary>
/// 鎖住「關掉簽章的管線仍然送出 query 參數」,以及它與簽章管線送出的位址逐字一致。
/// Pins that a pipeline with signing switched off still sends its query parameters, and that the URI it sends
/// matches the signing pipeline's byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// 0.3.2 以前,<c>WithQueryParameters</c> 的參數只有 <see cref="SigningHandler"/> 會寫進位址,而
/// <see cref="HttpPipelineOptions.EnableSigning"/> 為 <see langword="false"/> 時它根本不掛:未簽章管線的參數全部安靜地消失。
/// 下游實際踩到的是幣安主網公開行情(刻意不帶憑證、關掉簽章)查 K 線時 <c>symbol</c> 沒送出,對方回 <c>-1102</c>。
/// Up to 0.3.2 only <see cref="SigningHandler"/> wrote <c>WithQueryParameters</c> into the URI, and with
/// <see cref="HttpPipelineOptions.EnableSigning"/> set to <see langword="false"/> it is not attached: every
/// parameter on an unsigned pipeline vanished. Downstream, a Binance production public market data client —
/// credential-free with signing off, on purpose — sent a kline query without <c>symbol</c> and got <c>-1102</c>.
/// </para>
/// <para>
/// <b>故意弄壞驗證過。</b>拿掉 <c>AddOzakboyHttpPipeline</c> 在簽章關閉時掛上 <see cref="QueryParametersHandler"/>
/// 的那一段,五條測試變紅(送出的位址不帶 query,或原有的 query 沒被換掉);唯一維持綠燈的管線層級測試是
/// 「不帶參數的請求保留自己的 query」,它本來就不經過寫入。改回來即全綠。
/// <b>Verified by breaking it on purpose.</b> With the branch in <c>AddOzakboyHttpPipeline</c> that attaches
/// <see cref="QueryParametersHandler"/> when signing is off removed, five tests went red (the URI went out with no
/// query, or with its stale query left in place); the only pipeline-level test that stayed green is the one for a
/// request without parameters keeping its own query, which never involves writing. Restoring it turned all green.
/// </para>
/// </remarks>
[TestClass]
public sealed class UnsignedPipelineQueryTests
{
    private const string SecretKey = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";
    private const string ApiKey = "vmPUZE6mv9SD5VNHk4HlWFsOr6aKE2zvsw0MuIgwCIPy6utIco14y7Ju91duEh8A";
    private const string ClientName = "query";

    [TestMethod]
    public async Task UnsignedPipeline_SendsEveryParameterInOrderWithTheCanonicalEncoding()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var provider = BuildProvider(clock, stub, enableSigning: false);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        var parameters = TrickyParameters();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/fapi/v1/klines")
            .WithQueryParameters(parameters);
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(1, stub.CallCount);
        Assert.AreEqual(
            "https://example.test/fapi/v1/klines?symbol=BTCUSDT&interval=1m&limit=5&price=0.1&note=a%20b%26c%3Dd&%E5%90%8D=%E5%80%BC",
            stub.Requests[0].RequestUri!.AbsoluteUri,
            "未簽章管線必須送出全部參數,順序與編碼與 QueryParameters.ToQueryString() 相同。The unsigned pipeline must send every parameter, in the order and encoding of QueryParameters.ToQueryString().");
        Assert.AreEqual("?" + parameters.ToQueryString(), stub.Requests[0].RequestUri!.Query);
    }

    [TestMethod]
    public async Task SignedAndUnsignedPipelines_SendTheSameQueryForTheSameParameters()
    {
        var clock = new FakeTimeProvider();
        var unsignedStub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        var signedStub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var unsignedProvider = BuildProvider(clock, unsignedStub, enableSigning: false);
        using var signedProvider = BuildProvider(clock, signedStub, enableSigning: true);
        using var unsignedClient = unsignedProvider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using var signedClient = signedProvider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        // 同一個未標記簽章的請求:兩條管線送出的位址必須逐字相同。
        // The same request, not marked for signing: both pipelines must send the identical URI.
        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithQueryParameters(TrickyParameters()))
        using (var response = await unsignedClient.SendAsync(request, CancellationToken.None))
        {
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithQueryParameters(TrickyParameters()))
        using (var response = await signedClient.SendAsync(request, CancellationToken.None))
        {
        }

        Assert.AreEqual(signedStub.Requests[0].RequestUri!.AbsoluteUri, unsignedStub.Requests[0].RequestUri!.AbsoluteUri);

        // 標記簽章的請求在簽章管線上:簽過的那一段就是未簽章管線送出的 query,參數不重複、簽章只接在最後一次。
        // A signed request on the signing pipeline: the signed part is exactly what the unsigned pipeline sends,
        // no parameter is repeated, and the signature is appended exactly once.
        using (var signed = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithQueryParameters(TrickyParameters()).WithSignature())
        using (var response = await signedClient.SendAsync(signed, CancellationToken.None))
        {
        }

        var signedQuery = signedStub.Requests[1].RequestUri!.Query.TrimStart('?');
        var unsignedQuery = unsignedStub.Requests[0].RequestUri!.Query.TrimStart('?');
        StringAssert.StartsWith(signedQuery, unsignedQuery + "&signature=", StringComparison.Ordinal);
        Assert.AreEqual(1, CountOccurrences(signedQuery, "symbol="), $"參數被寫了兩次:{signedQuery}。A parameter was written twice: {signedQuery}.");
        Assert.AreEqual(1, CountOccurrences(signedQuery, "signature="), $"簽章被附加了兩次:{signedQuery}。The signature was appended twice: {signedQuery}.");
        Assert.AreEqual(ApiKey, signedStub.Requests[1].Headers["X-API-Key"]);
    }

    [TestMethod]
    public async Task UnsignedPipeline_ExistingQueryOnTheUri_IsReplacedExactlyAsOnTheSigningPipeline()
    {
        var clock = new FakeTimeProvider();
        var unsignedStub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        var signedStub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var unsignedProvider = BuildProvider(clock, unsignedStub, enableSigning: false);
        using var signedProvider = BuildProvider(clock, signedStub, enableSigning: true);
        using var unsignedClient = unsignedProvider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using var signedClient = signedProvider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        var parameters = QueryParameters.CreateBuilder().Add("fresh", "1").Build();

        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api?stale=yes").WithQueryParameters(parameters))
        using (var response = await unsignedClient.SendAsync(request, CancellationToken.None))
        {
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api?stale=yes").WithQueryParameters(parameters))
        using (var response = await signedClient.SendAsync(request, CancellationToken.None))
        {
        }

        Assert.AreEqual("https://example.test/api?fresh=1", unsignedStub.Requests[0].RequestUri!.AbsoluteUri);
        Assert.AreEqual(signedStub.Requests[0].RequestUri!.AbsoluteUri, unsignedStub.Requests[0].RequestUri!.AbsoluteUri);
    }

    [TestMethod]
    public async Task UnsignedPipeline_EmptyParameters_StripTheExistingQueryAsOnTheSigningPipeline()
    {
        var clock = new FakeTimeProvider();
        var unsignedStub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        var signedStub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var unsignedProvider = BuildProvider(clock, unsignedStub, enableSigning: false);
        using var signedProvider = BuildProvider(clock, signedStub, enableSigning: true);
        using var unsignedClient = unsignedProvider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using var signedClient = signedProvider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api?stale=yes").WithQueryParameters(QueryParameters.Empty))
        using (var response = await unsignedClient.SendAsync(request, CancellationToken.None))
        {
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api?stale=yes").WithQueryParameters(QueryParameters.Empty))
        using (var response = await signedClient.SendAsync(request, CancellationToken.None))
        {
        }

        Assert.AreEqual("https://example.test/api", unsignedStub.Requests[0].RequestUri!.AbsoluteUri);
        Assert.AreEqual(signedStub.Requests[0].RequestUri!.AbsoluteUri, unsignedStub.Requests[0].RequestUri!.AbsoluteUri);
    }

    [TestMethod]
    public async Task UnsignedPipeline_RequestWithoutParameters_KeepsItsOwnQuery()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var provider = BuildProvider(clock, stub, enableSigning: false);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api/time?raw=1");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual("https://example.test/api/time?raw=1", stub.Requests[0].RequestUri!.AbsoluteUri);
    }

    [TestMethod]
    public async Task UnsignedPipeline_EveryRetryAttempt_SendsTheSameQuery()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.ReturnsSequence(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);
        using var provider = BuildProvider(clock, stub, enableSigning: false);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/fapi/v1/klines")
            .WithQueryParameters(TrickyParameters());
        using var response = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromMilliseconds(50));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(3, stub.Requests.Count);

        var expected = "?" + TrickyParameters().ToQueryString();
        foreach (var sent in stub.Requests)
        {
            Assert.AreEqual(expected, sent.RequestUri!.Query, "每一次嘗試都必須帶著完整且相同的 query。Every attempt must carry the full, identical query.");
        }
    }

    [TestMethod]
    public async Task SegmentedRegistration_WithAddRequestSigning_StillWritesTheQuery()
    {
        // 分段註冊的呼叫端自己掛簽章處理器,由它寫入 query,行為與 0.3.2 相同。
        // A caller registering sections individually attaches the signing handler itself, which writes the query
        // exactly as in 0.3.2.
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        var services = new ServiceCollection();
        services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub)
            .AddRetry(new RetryOptions { Policy = RetryPolicy.NoRetry })
            .AddRequestSigning(new SigningOptions { SecretKey = SecretKey })
            .AddSanitizedLogging();

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api")
            .WithQueryParameters(TrickyParameters());
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual("?" + TrickyParameters().ToQueryString(), stub.Requests[0].RequestUri!.Query);
    }

    [TestMethod]
    public async Task QueryParametersHandler_RelativeRequestUri_KeepsItRelative()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var invoker = new HttpMessageInvoker(new QueryParametersHandler { InnerHandler = stub });

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("api/ticker", UriKind.Relative))
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("symbol", "BTCUSDT").Build());
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.AreEqual("api/ticker?symbol=BTCUSDT", stub.Requests[0].RequestUri!.OriginalString);
    }

    [TestMethod]
    public async Task QueryParametersHandler_NoRequestUri_FailsWithTheSigningPathsCodeWithoutSending()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var invoker = new HttpMessageInvoker(new QueryParametersHandler { InnerHandler = stub });

        using var request = new HttpRequestMessage { Method = HttpMethod.Get, RequestUri = null };
        request.WithQueryParameters(QueryParameters.CreateBuilder().Add("symbol", "BTCUSDT").Build());

        var exception = await Assert.ThrowsExactlyAsync<ResultException>(
            () => invoker.SendAsync(request, CancellationToken.None));

        Assert.AreEqual(HttpErrorCodes.SigningMissingRequestUri, exception.Error.Code);
        Assert.AreEqual(0, stub.CallCount);
    }

    [TestMethod]
    public async Task QueryParametersHandler_RequestMarkedForSignature_GetsParametersButNoSignature()
    {
        // 這個處理器不簽章:未簽章管線上標了 WithSignature 的請求照樣寫入參數,但不帶簽章也不帶金鑰標頭。
        // The handler does not sign: a WithSignature request on an unsigned pipeline gets its parameters but
        // neither a signature nor a key header.
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var invoker = new HttpMessageInvoker(new QueryParametersHandler { InnerHandler = stub });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api")
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("symbol", "BTCUSDT").Build())
            .WithSignature();
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.AreEqual("?symbol=BTCUSDT", stub.Requests[0].RequestUri!.Query);
        Assert.IsFalse(stub.Requests[0].Headers.ContainsKey("X-API-Key"));
    }

    private static QueryParameters TrickyParameters() =>
        QueryParameters.CreateBuilder()
            .Add("symbol", "BTCUSDT")
            .Add("interval", "1m")
            .Add("limit", 5L)
            .Add("price", 0.1m)
            .Add("note", "a b&c=d")
            .Add("名", "值")
            .Build();

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static ServiceProvider BuildProvider(FakeTimeProvider clock, StubHttpMessageHandler stub, bool enableSigning)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub)
            .AddOzakboyHttpPipeline(options =>
            {
                options.EnableSigning = enableSigning;
                if (enableSigning)
                {
                    options.Signing.ApiKey = ApiKey;
                    options.Signing.SecretKey = SecretKey;
                }

                options.RateLimiting.AcquisitionTimeout = TimeSpan.FromHours(1);
                options.RateLimiting.Buckets.Add(new RateLimitBucket("minute", 1000, TimeSpan.FromMinutes(1)));
                options.Retry.Policy = new RetryPolicy
                {
                    MaxAttempts = 3,
                    BaseDelay = TimeSpan.FromMilliseconds(100),
                    Strategy = BackoffStrategy.Exponential,
                    JitterRatio = 0d,
                };
                options.Timeouts.AttemptTimeout = TimeSpan.FromHours(1);
                options.Timeouts.OverallTimeout = TimeSpan.FromHours(2);
            });

        return services.BuildServiceProvider();
    }
}

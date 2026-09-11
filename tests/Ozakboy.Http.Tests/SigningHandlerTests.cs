using System.Net;
using Ozakboy.Http.Signing;
using Ozakboy.Http.Tests.TestSupport;

namespace Ozakboy.Http.Tests;

[TestClass]
public sealed class SigningHandlerTests
{
    private const string SecretKey = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";
    private const string ApiKey = "vmPUZE6mv9SD5VNHk4HlWFsOr6aKE2zvsw0MuIgwCIPy6utIco14y7Ju91duEh8A";
    private const string ExpectedSpotSignature = "c8db56825ae71d6d79447849e617115f4a920fa2acdcab2b053c4b2838bd6b71";

    [TestMethod]
    public async Task SendAsync_SignedRequest_SendsTheExactStringItSigned()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var client = CreateClient(stub, new SigningOptions { ApiKey = ApiKey, SecretKey = SecretKey });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api/v3/order")
            .WithQueryParameters(SpotGoldenParameters())
            .WithSignature();

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var sent = stub.Requests[0].RequestUri!;
        Assert.AreEqual(
            "https://example.test/api/v3/order?symbol=LTCBTC&side=BUY&type=LIMIT&timeInForce=GTC&quantity=1&price=0.1&recvWindow=5000&timestamp=1499827319559&signature=" + ExpectedSpotSignature,
            sent.AbsoluteUri);
    }

    [TestMethod]
    public async Task SendAsync_SignedRequest_AttachesTheApiKeyHeader()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var client = CreateClient(stub, new SigningOptions
        {
            ApiKey = ApiKey,
            SecretKey = SecretKey,
            ApiKeyHeaderName = "X-Custom-Key",
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api")
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("a", "1").Build())
            .WithSignature();

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(ApiKey, stub.Requests[0].Headers["X-Custom-Key"]);
    }

    [TestMethod]
    public async Task SendAsync_ApiKeyHeaderDisabled_SendsNoKeyHeader()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var client = CreateClient(stub, new SigningOptions
        {
            ApiKey = ApiKey,
            SecretKey = SecretKey,
            SendApiKeyHeader = false,
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api")
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("a", "1").Build())
            .WithSignature();

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.IsFalse(stub.Requests[0].Headers.ContainsKey("X-API-Key"));
    }

    [TestMethod]
    public async Task SendAsync_UnsignedRequestWithParameters_StillEncodesThemIntoTheUri()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var client = CreateClient(stub, new SigningOptions { SecretKey = SecretKey });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api/ticker")
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("symbol", "BTCUSDT").Build());

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual("https://example.test/api/ticker?symbol=BTCUSDT", stub.Requests[0].RequestUri!.AbsoluteUri);
        Assert.IsFalse(stub.Requests[0].RequestUri!.Query.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SendAsync_RequestWithoutParametersOrSignature_PassesThroughUntouched()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var client = CreateClient(stub, new SigningOptions { SecretKey = SecretKey });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api/time?raw=1");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual("https://example.test/api/time?raw=1", stub.Requests[0].RequestUri!.AbsoluteUri);
    }

    [TestMethod]
    public async Task SendAsync_ExistingQueryOnTheUri_IsReplacedByTheSignedParameters()
    {
        // 位址上原有的 query 不會被納入簽章,若留著就會出現「送出的字串比簽過的字串多東西」,
        // 對方一定驗不過。這裡確認它被換掉而不是附加上去。
        // A query already on the URI is not part of the signature; leaving it there would send more than was
        // signed, and the peer would reject it. This checks it is replaced rather than appended to.
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var client = CreateClient(stub, new SigningOptions { SecretKey = SecretKey });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api?stale=yes")
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("fresh", "1").Build());

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual("https://example.test/api?fresh=1", stub.Requests[0].RequestUri!.AbsoluteUri);
    }

    [TestMethod]
    public async Task SendAsync_FormBodyPlacement_PutsTheSignedPayloadInTheBody()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var client = CreateClient(stub, new SigningOptions
        {
            SecretKey = SecretKey,
            Placement = SignedPayloadPlacement.FormBody,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/api/v3/order")
            .WithQueryParameters(SpotGoldenParameters())
            .WithSignature();

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual("https://example.test/api/v3/order", stub.Requests[0].RequestUri!.AbsoluteUri);
        StringAssert.EndsWith(stub.Requests[0].Body, "&signature=" + ExpectedSpotSignature);
    }

    [TestMethod]
    public async Task SendAsync_SignedRequestWithNoParameters_StillAppendsTheSignature()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var client = CreateClient(stub, new SigningOptions { SecretKey = SecretKey });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithSignature();

        using var response = await client.SendAsync(request, CancellationToken.None);

        StringAssert.StartsWith(stub.Requests[0].RequestUri!.Query, "?signature=");
    }

    [TestMethod]
    public async Task SendAsync_MissingSecret_FailsWithoutSendingAnything()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var client = CreateClient(stub, new SigningOptions { SecretKey = string.Empty });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithSignature();

        var exception = await Assert.ThrowsExactlyAsync<ResultException>(
            () => client.SendAsync(request, CancellationToken.None));

        Assert.AreEqual(HttpErrorCodes.SigningSecretMissing, exception.Error.Code);
        Assert.AreEqual(0, stub.CallCount, "簽不出來就不該把請求送出去。A request that cannot be signed must never be sent.");
    }

    [TestMethod]
    public void Constructor_InvalidOptions_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => new SigningHandler(new SigningOptions { SignatureParameterName = " " }));

    [TestMethod]
    public void Constructor_NullOptions_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => new SigningHandler((SigningOptions)null!));


    private static QueryParameters SpotGoldenParameters() =>
        QueryParameters.CreateBuilder()
            .Add("symbol", "LTCBTC")
            .Add("side", "BUY")
            .Add("type", "LIMIT")
            .Add("timeInForce", "GTC")
            .Add("quantity", 1m)
            .Add("price", 0.1m)
            .Add("recvWindow", 5000L)
            .Add("timestamp", 1499827319559L)
            .Build();

    private static HttpClient CreateClient(StubHttpMessageHandler stub, SigningOptions options) =>
        new(new SigningHandler(options) { InnerHandler = stub });
}

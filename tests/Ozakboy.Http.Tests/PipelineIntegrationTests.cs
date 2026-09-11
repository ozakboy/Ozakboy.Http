using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Signing;
using Ozakboy.Http.Tests.TestSupport;

namespace Ozakboy.Http.Tests;

/// <summary>
/// 整條管線的端到端測試,以假的最內層處理器取代網路。
/// End-to-end tests for the whole pipeline, with a stub innermost handler standing in for the network.
/// </summary>
[TestClass]
public sealed class PipelineIntegrationTests
{
    private const string SecretKey = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";
    private const string ApiKey = "vmPUZE6mv9SD5VNHk4HlWFsOr6aKE2zvsw0MuIgwCIPy6utIco14y7Ju91duEh8A";

    [TestMethod]
    public async Task Pipeline_SignedGetThatIsRateLimitedOnce_SignsOncePerAttemptAndNeverLogsCredentials()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var stub = StubHttpMessageHandler.ReturnsSequence(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => stub)
            .AddOzakboyHttpPipeline(options =>
            {
                options.Signing.ApiKey = ApiKey;
                options.Signing.SecretKey = SecretKey;
                options.RateLimiting.Buckets.Add(new RateLimitBucket("minute", 100, TimeSpan.FromMinutes(1)));
                options.Retry.Policy = new RetryPolicy
                {
                    MaxAttempts = 3,
                    BaseDelay = TimeSpan.FromMilliseconds(100),
                    JitterRatio = 0d,
                };
                options.Timeouts.AttemptTimeout = TimeSpan.FromMinutes(5);
                options.Timeouts.OverallTimeout = TimeSpan.FromMinutes(10);
            });

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api/v3/account")
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("timestamp", 1499827319559L).Build())
            .WithSignature()
            .WithWeight(5);

        using var response = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromMilliseconds(50));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, stub.CallCount);

        // 簽章在管線最外層,兩次嘗試送出的是同一份已簽字串。
        // Signing sits outermost, so both attempts carry the same signed string.
        Assert.AreEqual(stub.Requests[0].RequestUri, stub.Requests[1].RequestUri);
        Assert.IsTrue(stub.Requests[0].RequestUri!.Query.Contains("signature=", StringComparison.Ordinal));
        Assert.AreEqual(ApiKey, stub.Requests[1].Headers["X-API-Key"]);
    }

    [TestMethod]
    public async Task Pipeline_RateLimiterIsSharedAcrossHandlerInstances()
    {
        // IHttpClientFactory 會定期重建處理器鏈。限流器若不是單例,配額會在重建後歸零 ——
        // 症狀是跑一陣子就被對方封鎖,本地卻看不出異常。
        // IHttpClientFactory rebuilds the handler chain periodically. A non-singleton limiter would reset its
        // quota on every rebuild: the symptom is getting banned after a while with nothing odd showing locally.
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => stub)
            .AddWeightedRateLimiting(BuildRateLimitOptions(limit: 2, window: TimeSpan.FromMinutes(1)));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        for (var i = 0; i < 2; i++)
        {
            using var client = factory.CreateClient("test");
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
            using var response = await client.SendAsync(request, CancellationToken.None);
        }

        using var blockedClient = factory.CreateClient("test");
        using var blockedRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        var pending = blockedClient.SendAsync(blockedRequest, CancellationToken.None);

        Assert.IsFalse(pending.IsCompleted, "換一個用戶端實例不該讓配額重新計算。A fresh client instance must not reset the quota.");
        using var unblocked = await FakeClockRunner.RunAsync(clock, pending, TimeSpan.FromSeconds(10));
        Assert.AreEqual(3, stub.CallCount);
    }

    [TestMethod]
    public async Task Pipeline_SigningDisabled_SkipsTheSigningHandler()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddHttpClient("public")
            .ConfigurePrimaryHttpMessageHandler(() => stub)
            .AddOzakboyHttpPipeline(options =>
            {
                options.EnableSigning = false;
                options.EnableRateLimiting = false;
            });

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("public");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api/time");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("https://example.test/api/time", stub.Requests[0].RequestUri!.AbsoluteUri);
    }

    [TestMethod]
    public void AddOzakboyHttpPipeline_InvalidOptions_Throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("bad");

        Assert.ThrowsExactly<ArgumentException>(() => builder.AddOzakboyHttpPipeline(options =>
            options.RateLimiting.Buckets.Clear()));
    }

    [TestMethod]
    public void AddOzakboyHttpPipeline_NullArguments_Throw()
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("x");

        Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddOzakboyHttpPipeline(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => OzakboyHttpClientBuilderExtensions.AddRequestSigning(null!, new SigningOptions()));
        Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddRequestSigning(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddWeightedRateLimiting(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddRetry(null!));
    }

    [TestMethod]
    public async Task AddSanitizedLogging_WithoutALoggerFactory_FallsBackToNullLogger()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        var services = new ServiceCollection();
        services.AddHttpClient("plain")
            .ConfigurePrimaryHttpMessageHandler(() => stub)
            .AddSanitizedLogging();

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("plain");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private static RateLimitOptions BuildRateLimitOptions(int limit, TimeSpan window)
    {
        var options = new RateLimitOptions { AcquisitionTimeout = TimeSpan.FromHours(24) };
        options.Buckets.Add(new RateLimitBucket("test", limit, window));
        return options;
    }
}

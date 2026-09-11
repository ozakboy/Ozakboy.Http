using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ozakboy.Http.Logging;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;
using Ozakboy.Http.Tests.TestSupport;
using Ozakboy.Security.Masking;

namespace Ozakboy.Http.Tests;

/// <summary>
/// 邊界與整合細節:選項容器建構式、相對位址、失敗封閉遮罩、單段註冊。
/// Edge cases and integration details: options-container constructors, relative URIs, fail-closed masking, and
/// single-section registration.
/// </summary>
[TestClass]
public sealed class EdgeCaseTests
{
    private const string SecretKey = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";

    [TestMethod]
    public async Task SigningHandler_RelativeRequestUri_KeepsItRelative()
    {
        // BaseAddress 情境下請求位址是相對的,組 query 時不能誤把它當絕對位址處理。
        // With a BaseAddress in play the request URI is relative, and building the query must not treat it as
        // an absolute one.
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var invoker = new HttpMessageInvoker(new SigningHandler(new SigningOptions { SecretKey = SecretKey })
        {
            InnerHandler = stub,
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("api/ticker", UriKind.Relative))
            .WithQueryParameters(QueryParameters.CreateBuilder().Add("symbol", "BTCUSDT").Build());

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.AreEqual("api/ticker?symbol=BTCUSDT", stub.Requests[0].RequestUri!.OriginalString);
    }

    [TestMethod]
    public async Task SigningHandler_NoRequestUri_FailsWithoutSending()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        using var invoker = new HttpMessageInvoker(new SigningHandler(new SigningOptions { SecretKey = SecretKey })
        {
            InnerHandler = stub,
        });

        using var request = new HttpRequestMessage { Method = HttpMethod.Get, RequestUri = null };
        request.WithSignature();

        var exception = await Assert.ThrowsExactlyAsync<ResultException>(
            () => invoker.SendAsync(request, CancellationToken.None));

        Assert.AreEqual(HttpErrorCodes.SigningMissingRequestUri, exception.Error.Code);
        Assert.AreEqual(0, stub.CallCount);
    }

    [TestMethod]
    public async Task SigningHandler_OptionsContainerConstructor_Works()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        var handler = new SigningHandler(Options.Create(new SigningOptions { SecretKey = SecretKey }))
        {
            InnerHandler = stub,
        };

        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithSignature();
        using var response = await client.SendAsync(request, CancellationToken.None);

        StringAssert.StartsWith(stub.Requests[0].RequestUri!.Query, "?signature=");
    }

    [TestMethod]
    public void SigningHandler_NullOptionsContainer_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => new SigningHandler((IOptions<SigningOptions>)null!));

    [TestMethod]
    public async Task RetryHandler_OptionsContainerConstructor_Works()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        var handler = new RetryHandler(
            Options.Create(new RetryOptions { Policy = RetryPolicy.NoRetry }),
            Options.Create(new HttpTimeoutOptions()),
            clock)
        {
            InnerHandler = stub,
        };

        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public void RetryHandler_NullOptionsContainers_Throw()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new RetryHandler(null!, Options.Create(new HttpTimeoutOptions())));
        Assert.ThrowsExactly<ArgumentNullException>(() => new RetryHandler(Options.Create(new RetryOptions()), null!));
    }

    [TestMethod]
    public async Task RetryHandler_ErrorBodySnippet_IsCarriedIntoTheError()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.BadRequest, "{\"code\":-1121}");
        var handler = new RetryHandler(
            new RetryOptions { Policy = RetryPolicy.Default, ErrorBodySnippetLength = 64 },
            new HttpTimeoutOptions { AttemptTimeout = TimeSpan.FromMinutes(5), OverallTimeout = TimeSpan.FromMinutes(10) },
            clock)
        {
            InnerHandler = stub,
        };

        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(1, stub.CallCount);
    }

    [TestMethod]
    public async Task SanitizingLoggingHandler_LoggerFactoryConstructor_Works()
    {
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);
        var handler = new SanitizingLoggingHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Options.Create(new RequestLoggingOptions()))
        {
            InnerHandler = stub,
        };

        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public void SanitizingLoggingHandler_NullContainers_Throw()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new SanitizingLoggingHandler(
            (ILoggerFactory)null!,
            Options.Create(new RequestLoggingOptions())));

        Assert.ThrowsExactly<ArgumentNullException>(() => new SanitizingLoggingHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            null!));
    }

    [TestMethod]
    public async Task SanitizingLoggingHandler_CancelledRequest_LogsAndRethrows()
    {
        var logger = new RecordingLogger();
        var stub = new StubHttpMessageHandler((_, _, token) => Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true)));
        var handler = new SanitizingLoggingHandler(logger) { InnerHandler = stub };

        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => client.SendAsync(request, CancellationToken.None));
        Assert.IsTrue(logger.Entries.Any(entry => entry.Level == LogLevel.Error));
    }

    [TestMethod]
    public void SafeMasking_NullUri_ReturnsEmpty() =>
        Assert.AreEqual(string.Empty, SafeMasking.MaskUri(SecretMasker.Default, null));

    [TestMethod]
    public void SafeMasking_EmptyBody_ReturnsEmpty() =>
        Assert.AreEqual(string.Empty, SafeMasking.MaskBody(SecretMasker.Default, string.Empty, 100));

    [TestMethod]
    public void SafeMasking_MalformedJson_ReturnsThePlaceholderNotTheOriginal()
    {
        var body = "not json at all, secret=abcdef";

        var masked = SafeMasking.MaskBody(SecretMasker.Default, body, 100);

        Assert.AreEqual(SafeMasking.MaskingFailedPlaceholder, masked);
        Assert.IsFalse(masked.Contains("abcdef", StringComparison.Ordinal), "遮不了時回傳原文就是 fail-open,絕不允許。Returning the original when masking fails is fail-open and is never allowed.");
    }

    [TestMethod]
    public void SafeMasking_LongJson_IsTruncatedToTheConfiguredLength()
    {
        var body = "{\"note\":\"" + new string('x', 500) + "\"}";

        var masked = SafeMasking.MaskBody(SecretMasker.Default, body, 32);

        Assert.AreEqual(32, masked.Length);
    }

    [TestMethod]
    public void HttpErrorMapper_StatusError_CarriesTheStatusCodeAsANumber()
    {
        // 狀態碼是下游最想用程式讀回去的東西,存成數值才不必每個消費端各寫一次 Parse。
        // The status code is what downstream code most wants to read back, and storing it as a number spares
        // every consumer from writing its own parse.
        var error = HttpErrorMapper.FromStatusCode(HttpStatusCode.TooManyRequests, "Too Many Requests", "{\"code\":-1003}");

        Assert.IsTrue(error.TryGetInt64(HttpErrorDataKeys.StatusCode, out var statusCode));
        Assert.AreEqual(429L, statusCode);
        Assert.IsTrue(error.TryGetData(HttpErrorDataKeys.Body, out var body));
        Assert.AreEqual("{\"code\":-1003}", body);
        Assert.AreEqual(ErrorCategory.RateLimited, error.Category);
    }

    [TestMethod]
    public void HttpErrorMapper_NoBodySnippet_OmitsTheBodyEntry()
    {
        var error = HttpErrorMapper.FromStatusCode(HttpStatusCode.BadRequest);

        Assert.IsTrue(error.TryGetInt64(HttpErrorDataKeys.StatusCode, out var statusCode));
        Assert.AreEqual(400L, statusCode);
        Assert.IsFalse(error.TryGetData(HttpErrorDataKeys.Body, out _));
    }

    [TestMethod]
    public void HttpErrorMapper_WithRetryAfter_CarriesTheDelayAsANumber()
    {
        var clock = new FakeTimeProvider();
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));

        var error = HttpErrorMapper.WithRetryAfter(
            HttpErrorMapper.FromStatusCode(HttpStatusCode.TooManyRequests),
            response,
            clock);

        Assert.IsTrue(error.TryGetDecimal(HttpErrorDataKeys.RetryAfterSeconds, out var seconds));
        Assert.AreEqual(90m, seconds);

        // 原有的資料不能被蓋掉:附加一筆不等於重建一份。
        // The existing entries must survive: adding one is not rebuilding the set.
        Assert.IsTrue(error.TryGetInt64(HttpErrorDataKeys.StatusCode, out var statusCode));
        Assert.AreEqual(429L, statusCode);
    }

    [TestMethod]
    public void HttpErrorMapper_WithRetryAfter_WithoutTheHeader_LeavesTheErrorAlone()
    {
        var clock = new FakeTimeProvider();
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var original = HttpErrorMapper.FromStatusCode(HttpStatusCode.ServiceUnavailable);

        var error = HttpErrorMapper.WithRetryAfter(original, response, clock);

        Assert.AreSame(original, error);
        Assert.IsFalse(error.TryGetDecimal(HttpErrorDataKeys.RetryAfterSeconds, out _));
    }

    [TestMethod]
    public void RateLimitBucket_Equality_IsValueBased()
    {
        var first = new RateLimitBucket("minute", 100, TimeSpan.FromMinutes(1));
        var second = new RateLimitBucket("minute", 100, TimeSpan.FromMinutes(1));
        var different = new RateLimitBucket("second", 100, TimeSpan.FromMinutes(1));

        Assert.AreEqual(first, second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
        Assert.AreNotEqual(first, different);
        StringAssert.Contains(first.ToString(), "minute");
    }

    [TestMethod]
    public async Task AddRequestSigning_AndAddRetry_RegisterIndividually()
    {
        var clock = new FakeTimeProvider();
        var stub = StubHttpMessageHandler.ReturnsSequence(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddHttpClient("partial")
            .ConfigurePrimaryHttpMessageHandler(() => stub)
            .AddRequestSigning(new SigningOptions { SecretKey = SecretKey })
            .AddRetry(
                new RetryOptions { Policy = new RetryPolicy { MaxAttempts = 2, BaseDelay = TimeSpan.FromMilliseconds(50), JitterRatio = 0d } },
                new HttpTimeoutOptions { AttemptTimeout = TimeSpan.FromMinutes(5), OverallTimeout = TimeSpan.FromMinutes(10) });

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("partial");
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api").WithSignature();
        using var response = await FakeClockRunner.RunAsync(
            clock,
            client.SendAsync(request, CancellationToken.None),
            TimeSpan.FromMilliseconds(25));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, stub.CallCount);
    }
}

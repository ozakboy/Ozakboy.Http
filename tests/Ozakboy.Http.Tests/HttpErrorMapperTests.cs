using System.Net;
using System.Net.Http.Headers;

namespace Ozakboy.Http.Tests;

[TestClass]
public sealed class HttpErrorMapperTests
{
    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests, ErrorCategory.RateLimited, true)]
    [DataRow(HttpStatusCode.RequestTimeout, ErrorCategory.Timeout, true)]
    [DataRow(HttpStatusCode.InternalServerError, ErrorCategory.Unavailable, true)]
    [DataRow(HttpStatusCode.BadGateway, ErrorCategory.Unavailable, true)]
    [DataRow(HttpStatusCode.ServiceUnavailable, ErrorCategory.Unavailable, true)]
    [DataRow(HttpStatusCode.GatewayTimeout, ErrorCategory.Unavailable, true)]
    [DataRow(HttpStatusCode.BadRequest, ErrorCategory.Validation, false)]
    [DataRow(HttpStatusCode.Unauthorized, ErrorCategory.Unauthorized, false)]
    [DataRow(HttpStatusCode.Forbidden, ErrorCategory.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, ErrorCategory.NotFound, false)]
    [DataRow(HttpStatusCode.Conflict, ErrorCategory.Conflict, false)]
    [DataRow(HttpStatusCode.UnprocessableEntity, ErrorCategory.Validation, false)]
    public void FromStatusCode_MapsCategoryAndTransience(HttpStatusCode status, ErrorCategory expectedCategory, bool expectedTransient)
    {
        var error = HttpErrorMapper.FromStatusCode(status);

        Assert.AreEqual(expectedCategory, error.Category);
        Assert.AreEqual(expectedTransient, error.IsTransient);
        Assert.AreEqual(HttpErrorCodes.ForStatus(status), error.Code);
    }

    [TestMethod]
    public void FromStatusCode_KeepsTheStatusCodeInStructuredData()
    {
        var error = HttpErrorMapper.FromStatusCode(HttpStatusCode.TooManyRequests, "Too Many Requests", "{\"code\":-1003}");

        Assert.AreEqual("429", error.Data!["statusCode"]);
        Assert.AreEqual("{\"code\":-1003}", error.Data!["body"]);
        StringAssert.Contains(error.Message, "429");
    }

    [TestMethod]
    public void ForStatus_BuildsThePrefixedCode() =>
        Assert.AreEqual("http.status.503", HttpErrorCodes.ForStatus(HttpStatusCode.ServiceUnavailable));

    [TestMethod]
    public void FromException_NetworkFailure_IsTransient()
    {
        var error = HttpErrorMapper.FromException(new HttpRequestException("boom"));

        Assert.AreEqual(HttpErrorCodes.Network, error.Code);
        Assert.AreEqual(ErrorCategory.Network, error.Category);
        Assert.IsTrue(error.IsTransient);
    }

    [TestMethod]
    public void FromException_Timeout_IsTransient()
    {
        var error = HttpErrorMapper.FromException(new TimeoutException());

        Assert.AreEqual(HttpErrorCodes.Timeout, error.Code);
        Assert.IsTrue(error.IsTransient);
    }

    [TestMethod]
    public void FromException_Cancellation_IsNotTransient()
    {
        // 呼叫端自己按下取消,重試只會再取消一次。
        // The caller cancelled; retrying only gets cancelled again.
        var error = HttpErrorMapper.FromException(new OperationCanceledException());

        Assert.AreEqual(HttpErrorCodes.Cancelled, error.Code);
        Assert.AreEqual(ErrorCategory.Cancelled, error.Category);
        Assert.IsFalse(error.IsTransient);
    }

    [TestMethod]
    public void FromException_UnknownException_FallsBackToNetwork()
    {
        var error = HttpErrorMapper.FromException(new InvalidOperationException("odd"));

        Assert.AreEqual(HttpErrorCodes.Network, error.Code);
        Assert.IsNotNull(error.Exception);
    }

    [TestMethod]
    public void FromException_Null_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => HttpErrorMapper.FromException(null!));

    [TestMethod]
    public void TryGetRetryAfter_DeltaSeconds_IsReturnedAsIs()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));

        Assert.IsTrue(HttpErrorMapper.TryGetRetryAfter(response, TimeProvider.System, out var delay));
        Assert.AreEqual(TimeSpan.FromSeconds(42), delay);
    }

    [TestMethod]
    public void TryGetRetryAfter_HttpDate_IsConvertedAgainstTheClock()
    {
        var clock = new FakeTimeProvider();
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(90));

        Assert.IsTrue(HttpErrorMapper.TryGetRetryAfter(response, clock, out var delay));
        Assert.AreEqual(TimeSpan.FromSeconds(90), delay);
    }

    [TestMethod]
    public void TryGetRetryAfter_DateAlreadyPassed_ClampsToZero()
    {
        var clock = new FakeTimeProvider();
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(-90));

        Assert.IsTrue(HttpErrorMapper.TryGetRetryAfter(response, clock, out var delay));
        Assert.AreEqual(TimeSpan.Zero, delay);
    }

    [TestMethod]
    public void TryGetRetryAfter_HeaderAbsent_ReturnsFalse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        Assert.IsFalse(HttpErrorMapper.TryGetRetryAfter(response, TimeProvider.System, out var delay));
        Assert.AreEqual(TimeSpan.Zero, delay);
    }

    [TestMethod]
    public async Task FromResponseAsync_IncludesATruncatedBodySnippet()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(new string('x', 1000)),
        };

        var error = await HttpErrorMapper.FromResponseAsync(response, 32, CancellationToken.None);

        Assert.AreEqual(32, error.Data!["body"].Length);
    }

    [TestMethod]
    public async Task FromResponseAsync_ZeroSnippetLength_SkipsTheBody()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("payload"),
        };

        var error = await HttpErrorMapper.FromResponseAsync(response, 0, CancellationToken.None);

        Assert.IsFalse(error.Data!.ContainsKey("body"));
    }
}

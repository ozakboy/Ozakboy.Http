using System.Net;
using Microsoft.Extensions.Logging;
using Ozakboy.Http.Logging;
using Ozakboy.Http.Tests.TestSupport;

namespace Ozakboy.Http.Tests;

[TestClass]
public sealed class SanitizingLoggingHandlerTests
{
    private const string ApiKeyValue = "vmPUZE6mv9SD5VNHk4HlWFsOr6aKE2zvsw0MuIgwCIPy6utIco14y7Ju91duEh8A";
    private const string SignatureValue = "c8db56825ae71d6d79447849e617115f4a920fa2acdcab2b053c4b2838bd6b71";

    [TestMethod]
    public async Task SendAsync_MasksSensitiveQueryValuesButKeepsTheEndpointVisible()
    {
        var logger = new RecordingLogger();
        using var client = CreateClient(logger, out _);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://example.test/api/v3/order?symbol=BTCUSDT&apiKey={ApiKeyValue}&signature={SignatureValue}");

        using var response = await client.SendAsync(request, CancellationToken.None);

        var text = logger.AllText;
        Assert.IsFalse(text.Contains(ApiKeyValue, StringComparison.Ordinal), "API 金鑰不得出現在日誌。The API key must not appear in the log.");
        Assert.IsFalse(text.Contains(SignatureValue, StringComparison.Ordinal), "簽章不得出現在日誌。The signature must not appear in the log.");

        StringAssert.Contains(text, "/api/v3/order", StringComparison.Ordinal);
        StringAssert.Contains(text, "symbol=BTCUSDT", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SendAsync_SensitiveNameInDifferentCasing_IsStillMasked()
    {
        var logger = new RecordingLogger();
        using var client = CreateClient(logger, out _);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://example.test/api?APIKEY={ApiKeyValue}&SiGnAtUrE={SignatureValue}");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.IsFalse(logger.AllText.Contains(ApiKeyValue, StringComparison.Ordinal), "參數名比對必須忽略大小寫。Parameter-name matching must be case-insensitive.");
        Assert.IsFalse(logger.AllText.Contains(SignatureValue, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SendAsync_AdditionalSensitiveName_IsMasked()
    {
        var logger = new RecordingLogger();
        var options = new RequestLoggingOptions();
        options.AdditionalSensitiveParameterNames.Add("clientOrderTag");
        using var client = CreateClient(logger, out _, options);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api?clientOrderTag=super-secret-value-1234");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.IsFalse(logger.AllText.Contains("super-secret-value-1234", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SendAsync_RegisteredKnownSecret_IsMaskedWhereverItAppears()
    {
        // 名稱比對只擋得住放在預期位置的祕密;金鑰被對方 echo 回錯誤訊息裡時要靠值比對。
        // Name matching only stops secrets in expected places; a key echoed back inside an error message needs
        // value matching.
        var logger = new RecordingLogger();
        var options = new RequestLoggingOptions { LogResponseBody = true };
        using var client = CreateClient(
            logger,
            out var handler,
            options,
            StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.BadRequest, $"{{\"msg\":\"invalid key {ApiKeyValue}\"}}"));

        handler.RegisterKnownSecret(ApiKeyValue);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.IsFalse(logger.AllText.Contains(ApiKeyValue, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SendAsync_NonJsonBody_IsDiscardedRatherThanLoggedRaw()
    {
        // 表單編碼的下單請求裡就躺著 signature,無法逐欄位判斷,因此整段捨棄。
        // A form-encoded order request has the signature sitting right in it and cannot be inspected field by
        // field, so the whole body is discarded.
        var logger = new RecordingLogger();
        var options = new RequestLoggingOptions { LogRequestBody = true };
        using var client = CreateClient(logger, out _, options);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/api")
        {
            Content = new StringContent($"symbol=BTCUSDT&signature={SignatureValue}"),
        };

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.IsFalse(logger.AllText.Contains(SignatureValue, StringComparison.Ordinal), "遮不了就該整段捨棄,絕不原樣輸出。What cannot be masked is discarded, never emitted raw.");
        StringAssert.Contains(logger.AllText, "<redacted:masking-failed>", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SendAsync_JsonBody_MasksOnlyTheSensitiveFields()
    {
        var logger = new RecordingLogger();
        var options = new RequestLoggingOptions { LogRequestBody = true };
        using var client = CreateClient(logger, out _, options);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/api")
        {
            Content = new StringContent($"{{\"symbol\":\"BTCUSDT\",\"signature\":\"{SignatureValue}\"}}"),
        };

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.IsFalse(logger.AllText.Contains(SignatureValue, StringComparison.Ordinal));
        StringAssert.Contains(logger.AllText, "BTCUSDT", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SendAsync_SuccessfulResponse_LogsAtDebugAndFailureAtWarning()
    {
        var logger = new RecordingLogger();
        using var client = CreateClient(logger, out _, null, StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.IsTrue(logger.Entries.Any(entry => entry.Level == LogLevel.Debug && entry.EventId == 1002));
        Assert.IsFalse(logger.Entries.Any(entry => entry.Level == LogLevel.Warning));
    }

    [TestMethod]
    public async Task SendAsync_UnsuccessfulResponse_LogsAtWarning()
    {
        var logger = new RecordingLogger();
        using var client = CreateClient(logger, out _, null, StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.TooManyRequests));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.IsTrue(logger.Entries.Any(entry => entry.Level == LogLevel.Warning && entry.EventId == 1003));
    }

    [TestMethod]
    public async Task SendAsync_TransportFailure_LogsAtErrorAndRethrows()
    {
        var logger = new RecordingLogger();
        var stub = new StubHttpMessageHandler((_, _, _) => throw new HttpRequestException("down"));
        using var client = CreateClient(logger, out _, null, stub);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.SendAsync(request, CancellationToken.None));
        // 記錄器拿到的是替身,不是原始例外物件;原始型別名稱仍保留供診斷。
        // The logger receives the stand-in, not the original object; the original type name survives for diagnosis.
        Assert.IsTrue(logger.Entries.Any(entry =>
            entry.Level == LogLevel.Error
            && entry.Exception is SanitizedException { OriginalExceptionType: "System.Net.Http.HttpRequestException" }));
        Assert.IsFalse(logger.Entries.Any(entry => entry.Exception is HttpRequestException));
    }

    [TestMethod]
    public void Constructor_NullLogger_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => new SanitizingLoggingHandler((ILogger)null!));

    [TestMethod]
    public void Constructor_InvalidOptions_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => new SanitizingLoggingHandler(new RecordingLogger(), new RequestLoggingOptions { MaxBodyLength = 0 }));

    private static HttpClient CreateClient(
        RecordingLogger logger,
        out SanitizingLoggingHandler handler,
        RequestLoggingOptions? options = null,
        StubHttpMessageHandler? stub = null)
    {
        handler = new SanitizingLoggingHandler(logger, options)
        {
            InnerHandler = stub ?? StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK),
        };

        return new HttpClient(handler);
    }
}

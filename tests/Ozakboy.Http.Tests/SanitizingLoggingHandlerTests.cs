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
    public async Task SendAsync_SuccessfulResponse_LogsAtDebugOnly()
    {
        var logger = new RecordingLogger();
        using var client = CreateClient(logger, out _, null, StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.IsTrue(logger.Entries.Any(entry => entry.Level == LogLevel.Debug && entry.EventId == 1002));
        Assert.IsFalse(logger.Entries.Any(entry => entry.EventId == 1003));
        Assert.IsFalse(logger.Entries.Any(entry => entry.Level == LogLevel.Warning));
    }

    [TestMethod]
    public async Task SendAsync_SuccessfulResponse_IsUnaffectedByUnsuccessfulLevelSettings()
    {
        // 等級設定只管非成功回應;把它們都調到 Error,成功回應仍然只有一筆 Debug。
        // The level settings govern unsuccessful responses only; with both raised to Error, a success still leaves
        // a single Debug entry.
        var options = new RequestLoggingOptions
        {
            UnsuccessfulResponseLevel = LogLevel.Error,
            ElevatedResponseLevel = LogLevel.Error,
        };
        options.ElevatedStatusCodes.Add(200);

        var entries = await SendAndCaptureAsync(HttpStatusCode.OK, options);

        Assert.IsTrue(entries.Any(entry => entry.Level == LogLevel.Debug && entry.EventId == 1002));
        Assert.IsFalse(entries.Any(entry => entry.EventId == 1003));
        Assert.IsFalse(entries.Any(entry => entry.Level >= LogLevel.Information));
    }

    [TestMethod]
    [DataRow(400)]
    [DataRow(401)]
    [DataRow(404)]
    [DataRow(409)]
    public async Task SendAsync_ClientErrorResponse_LogsAtInformationByDefault(int statusCode)
    {
        // 對方回 4xx 多半是業務上的預期結果(撤後查單、只減倉被拒),預設不該記成警告。
        // A 4xx is usually an expected business outcome (querying a cancelled order, a refused reduce-only order),
        // so by default it must not be a warning.
        var entries = await SendAndCaptureAsync((HttpStatusCode)statusCode);

        var entry = SingleUnsuccessfulEntry(entries);
        Assert.AreEqual(LogLevel.Information, entry.Level, $"狀態碼 {statusCode} 應記為 Information。Status {statusCode} should log at Information.");
        Assert.IsFalse(entries.Any(candidate => candidate.Level == LogLevel.Warning));
    }

    [TestMethod]
    [DataRow(500)]
    [DataRow(502)]
    [DataRow(503)]
    [DataRow(504)]
    [DataRow(429)]
    [DataRow(418)]
    public async Task SendAsync_ServerErrorOrRateLimitResponse_StaysAtWarningByDefault(int statusCode)
    {
        var entries = await SendAndCaptureAsync((HttpStatusCode)statusCode);

        Assert.AreEqual(LogLevel.Warning, SingleUnsuccessfulEntry(entries).Level, $"狀態碼 {statusCode} 應維持 Warning。Status {statusCode} should stay at Warning.");
    }

    [TestMethod]
    public async Task SendAsync_UnsuccessfulResponse_KeepsEventIdAndMessageFormat()
    {
        // 只換等級:事件編號、訊息格式與結構化欄位一律不變,依它們設的告警規則才不會失效。
        // Only the level changes: event id, message format and structured fields stay as they were, so alert rules
        // keyed on them keep working.
        var entries = await SendAndCaptureAsync(HttpStatusCode.NotFound);

        var entry = SingleUnsuccessfulEntry(entries);
        StringAssert.StartsWith(entry.Message, "HTTP 回應非成功 GET https://example.test/api -> 404,耗時 ", StringComparison.Ordinal);
        StringAssert.EndsWith(entry.Message, " ms", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SendAsync_UnsuccessfulLevelSetToWarning_RestoresTheOldBehaviour()
    {
        var options = new RequestLoggingOptions { UnsuccessfulResponseLevel = LogLevel.Warning };

        var entries = await SendAndCaptureAsync(HttpStatusCode.BadRequest, options);

        Assert.AreEqual(LogLevel.Warning, SingleUnsuccessfulEntry(entries).Level);
    }

    [TestMethod]
    public async Task SendAsync_AddedElevatedStatusCode_IsElevated()
    {
        var options = new RequestLoggingOptions();
        options.ElevatedStatusCodes.Add(409);

        var entries = await SendAndCaptureAsync(HttpStatusCode.Conflict, options);

        Assert.AreEqual(LogLevel.Warning, SingleUnsuccessfulEntry(entries).Level);
    }

    [TestMethod]
    [DataRow(429)]
    [DataRow(418)]
    [DataRow(503)]
    public async Task SendAsync_ElevationTurnedOff_UsesTheGeneralLevel(int statusCode)
    {
        var options = new RequestLoggingOptions { ElevateServerErrors = false };
        options.ElevatedStatusCodes.Clear();

        var entries = await SendAndCaptureAsync((HttpStatusCode)statusCode, options);

        Assert.AreEqual(LogLevel.Information, SingleUnsuccessfulEntry(entries).Level);
    }

    [TestMethod]
    public async Task SendAsync_ServerErrorElevationOff_StillElevatesListedCodes()
    {
        var options = new RequestLoggingOptions { ElevateServerErrors = false };

        var serverError = await SendAndCaptureAsync(HttpStatusCode.ServiceUnavailable, options);
        var rateLimited = await SendAndCaptureAsync(HttpStatusCode.TooManyRequests, options);

        Assert.AreEqual(LogLevel.Information, SingleUnsuccessfulEntry(serverError).Level);
        Assert.AreEqual(LogLevel.Warning, SingleUnsuccessfulEntry(rateLimited).Level);
    }

    [TestMethod]
    public async Task SendAsync_CustomElevatedLevel_IsUsed()
    {
        var options = new RequestLoggingOptions { ElevatedResponseLevel = LogLevel.Error };

        var entries = await SendAndCaptureAsync(HttpStatusCode.InternalServerError, options);

        Assert.AreEqual(LogLevel.Error, SingleUnsuccessfulEntry(entries).Level);
    }

    [TestMethod]
    public async Task SendAsync_LevelNone_WritesNoUnsuccessfulEntry()
    {
        var options = new RequestLoggingOptions
        {
            UnsuccessfulResponseLevel = LogLevel.None,
            ElevatedResponseLevel = LogLevel.None,
        };

        var clientError = await SendAndCaptureAsync(HttpStatusCode.BadRequest, options);
        var serverError = await SendAndCaptureAsync(HttpStatusCode.BadGateway, options);

        Assert.IsFalse(clientError.Any(entry => entry.EventId == 1003));
        Assert.IsFalse(serverError.Any(entry => entry.EventId == 1003));
    }

    [TestMethod]
    public async Task SendAsync_OptionsChangedAfterConstruction_DoNotAffectTheHandler()
    {
        // 處理器在建構時複製等級設定;之後改動那份可變名單,不會讓並行中的請求讀到半改的狀態。
        // The handler copies the level settings at construction; mutating the set afterwards cannot leave
        // in-flight requests reading a half-changed state.
        var logger = new RecordingLogger();
        var options = new RequestLoggingOptions();
        using var client = CreateClient(logger, out _, options, StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.TooManyRequests));

        options.ElevatedStatusCodes.Clear();
        options.ElevatedResponseLevel = LogLevel.Critical;

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(LogLevel.Warning, SingleUnsuccessfulEntry(logger.Entries).Level);
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

    private static async Task<List<RecordingLogger.RecordedEntry>> SendAndCaptureAsync(HttpStatusCode statusCode, RequestLoggingOptions? options = null)
    {
        var logger = new RecordingLogger();
        using var client = CreateClient(logger, out _, options, StubHttpMessageHandler.AlwaysReturns(statusCode));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");
        using var response = await client.SendAsync(request, CancellationToken.None);

        return logger.Entries;
    }

    private static RecordingLogger.RecordedEntry SingleUnsuccessfulEntry(IEnumerable<RecordingLogger.RecordedEntry> entries)
    {
        var matches = entries.Where(entry => entry.EventId == 1003).ToList();
        Assert.HasCount(1, matches, "非成功回應應恰好留下一筆事件 1003。An unsuccessful response should leave exactly one event 1003.");
        return matches[0];
    }

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

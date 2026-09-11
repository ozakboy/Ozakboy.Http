using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ozakboy.Http.Tests.TestSupport;
using Ozakboy.Security.Masking;

namespace Ozakboy.Http.Tests;

/// <summary>
/// 以一個可辨識的假祕密走過失敗路徑,檢查它是否從任何輸出點流出:日誌(訊息、結構化參數、範圍、例外文字)
/// 與回傳的錯誤(代碼、訊息、資料、例外)。
/// Walks a recognisable fake secret through the failure paths and checks every output point for it: logs
/// (message, structured parameters, scopes, exception text) and the returned error (code, message, data,
/// exception).
/// </summary>
/// <remarks>
/// 假祕密放在請求路徑裡,也放在連線例外的訊息裡 —— 那正是欄位名規則攔不到、只有已登記祕密的字面替換攔得到的位置。
/// The fake secret sits in the request path and in the transport exception's message — exactly where name rules
/// cannot reach and only literal replacement of registered secrets can.
/// </remarks>
[TestClass]
public sealed class SecretLeakTests
{
    private const string FakeSecret = "LEAKCANARY-7f3a9c2e5b41d8";
    private const string ClientName = "leaky";
    private const string SecretKey = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";
    private const string ApiKey = "vmPUZE6mv9SD5VNHk4HlWFsOr6aKE2zvsw0MuIgwCIPy6utIco14y7Ju91duEh8A";

    private static readonly string SecretUrl = $"https://api.example.test/bot{FakeSecret}/sendMessage";

    [TestMethod]
    public async Task FailurePaths_NeverLeakARegisteredSecret()
    {
        var clock = new FakeTimeProvider();
        var capture = new CapturingLoggerProvider();
        var stub = new StubHttpMessageHandler((request, _, _) => throw new HttpRequestException(
            $"Connection refused ({request.RequestUri})",
            new IOException($"socket to {request.RequestUri} closed")));

        using var provider = BuildProvider(clock, capture, stub);
        var masker = provider.GetOzakboyHttpMasker(ClientName);
        // 以建議做法建立門面:遮罩器與逾時由註冊處自動帶入,下面「位址中的祕密被遮掉」的斷言同時驗證了遮罩器確實帶進來了。
        // The facade is built the recommended way, with masker and timeouts brought in from the registration; the
        // "secret masked in the URI" assertions below also prove the masker really was carried in.
        var client = provider.CreateOzakboyHttpPipelineClient(ClientName);

        // 可重試的 GET:次數用盡 → 重試處理器產生的錯誤。
        // A retryable GET: attempts run out, so the error comes from the retry handler.
        using var get = new HttpRequestMessage(HttpMethod.Get, SecretUrl);
        var getResult = await FakeClockRunner.RunAsync(clock, client.SendAsync(get, CancellationToken.None), TimeSpan.FromMilliseconds(50));

        // 不可重試的 POST:原始例外穿過重試層,在門面邊界才被對映。
        // A non-retryable POST: the raw exception passes through retry and is mapped at the facade boundary.
        using var post = new HttpRequestMessage(HttpMethod.Post, SecretUrl) { Content = new StringContent("{}") };
        var postResult = await client.SendAsync(post, CancellationToken.None);

        Assert.IsTrue(getResult.IsFailure);
        Assert.IsTrue(postResult.IsFailure);
        Assert.AreEqual(ErrorCategory.Exhausted, getResult.Error!.Category);
        Assert.AreEqual(ErrorCategory.Network, postResult.Error!.Category);

        var outputs = new Dictionary<string, string>
        {
            ["logs"] = capture.AllText,
            ["get error"] = Describe(getResult.Error!),
            ["post error"] = Describe(postResult.Error!),
        };

        foreach (var output in outputs)
        {
            Assert.IsFalse(
                output.Value.Contains(FakeSecret, StringComparison.Ordinal),
                $"假祕密出現在 {output.Key}。The fake secret leaked into {output.Key}:\n{output.Value}");
        }

        // 不是因為什麼都沒輸出才通過:位址確實出現在錯誤裡,只是祕密那段被遮掉了。
        // Not passing merely because nothing was emitted: the URI does appear in the errors, with the secret masked.
        StringAssert.Contains(outputs["post error"], $"bot{masker.MaskSegment}/sendMessage", StringComparison.Ordinal);
        StringAssert.Contains(outputs["get error"], $"bot{masker.MaskSegment}/sendMessage", StringComparison.Ordinal);

        foreach (var error in new[] { getResult.Error!, postResult.Error! })
        {
            var stand = error.Exception as SanitizedException;
            Assert.IsNotNull(stand, "錯誤不得攜帶原始例外物件。The error must not carry the original exception object.");
            Assert.AreEqual(typeof(HttpRequestException).FullName, stand.OriginalExceptionType);
            Assert.IsNull(stand.InnerException, "替身不得帶內層例外。The stand-in must carry no inner exception.");
        }

        var failures = capture.Entries.Where(entry => entry.Level == LogLevel.Error).ToList();
        Assert.IsNotEmpty(failures, "失敗必須被記錄。Failures must be logged.");
        Assert.IsTrue(
            failures.All(entry => entry.Exception is SanitizedException),
            "記錄器只能拿到替身。The logger must only ever receive the stand-in.");
    }

    [TestMethod]
    public async Task DefaultFactoryLogging_IsRemoved()
    {
        // IHttpClientFactory 預設的日誌在 Information 層級寫出完整位址,.NET 只遮 query、不遮路徑。
        // IHttpClientFactory's default logging writes the full URI at Information level; .NET redacts the query,
        // not the path.
        var clock = new FakeTimeProvider();
        var capture = new CapturingLoggerProvider();
        var stub = StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK);

        using var provider = BuildProvider(clock, capture, stub);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, SecretUrl);
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(
            capture.Entries.Any(entry => entry.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal)),
            "預設的 factory 日誌必須已被移除。The factory's default logging must have been removed.");
        Assert.IsFalse(capture.AllText.Contains(FakeSecret, StringComparison.Ordinal), "完整位址不得出現在日誌。The full URI must not appear in the log.");
        Assert.IsTrue(
            capture.Entries.Any(entry => entry.Category.Contains(nameof(Ozakboy.Http.Logging.SanitizingLoggingHandler), StringComparison.Ordinal)),
            "本套件自己的日誌仍然在。This package's own logging is still there.");
    }

    [TestMethod]
    public void PipelineClient_TimeoutIsInfinite_EvenWhenConfiguredEarlier()
    {
        // HttpClient 預設 100 秒若短於整體逾時,會先觸發並表現成取消,逾時被錯歸為呼叫端取消。
        // The default 100 seconds, if shorter than the overall timeout, fires first as a cancellation and the
        // timeout is misfiled as a caller cancellation.
        var services = new ServiceCollection();
        services.AddHttpClient(ClientName, client => client.Timeout = TimeSpan.FromSeconds(5))
            .AddOzakboyHttpPipeline(options => options.EnableRateLimiting = false);

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        Assert.AreEqual(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [TestMethod]
    public void SigningKeys_AreRegisteredOnTheClientMaskerAutomatically()
    {
        using var provider = BuildProvider(new FakeTimeProvider(), new CapturingLoggerProvider(), StubHttpMessageHandler.AlwaysReturns(HttpStatusCode.OK));
        var masker = provider.GetOzakboyHttpMasker(ClientName);

        var masked = masker.MaskText($"key={ApiKey} secret={SecretKey} token={FakeSecret}");

        Assert.IsFalse(masked.Contains(ApiKey, StringComparison.Ordinal));
        Assert.IsFalse(masked.Contains(SecretKey, StringComparison.Ordinal));
        Assert.IsFalse(masked.Contains(FakeSecret, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SecretRegisteredAtRunTime_IsMaskedByTheSameClient()
    {
        const string listenKey = "RUNTIME-LISTENKEY-5d1e9b77a0c3";
        var clock = new FakeTimeProvider();
        var capture = new CapturingLoggerProvider();
        var stub = new StubHttpMessageHandler((request, _, _) => throw new HttpRequestException($"reset ({request.RequestUri})"));

        using var provider = BuildProvider(clock, capture, stub);
        Assert.IsTrue(provider.GetOzakboyHttpMasker(ClientName).RegisterKnownSecret(listenKey));

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://example.test/ws/{listenKey}");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.SendAsync(request, CancellationToken.None));
        Assert.IsTrue(capture.Entries.Count > 0);
        Assert.IsFalse(capture.AllText.Contains(listenKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public void KnownSecretTooShort_IsRejectedWithoutEchoingIt()
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient(ClientName);

        var exception = Assert.ThrowsExactly<ArgumentException>(() => builder.AddOzakboyHttpPipeline(options =>
        {
            options.EnableRateLimiting = false;
            options.KnownSecrets.Add("short1");
        }));

        Assert.IsFalse(exception.Message.Contains("short1", StringComparison.Ordinal), "錯誤訊息不得回述被拒絕的值。The message must not echo the rejected value.");
    }

    [TestMethod]
    public void GetOzakboyHttpMasker_UnknownClient_Throws()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        Assert.ThrowsExactly<InvalidOperationException>(() => provider.GetOzakboyHttpMasker("nobody"));
    }

    [TestMethod]
    public void HttpErrorMapper_FromException_NeverKeepsTheOriginalObject()
    {
        var masker = new SecretMasker();
        masker.RegisterKnownSecret(FakeSecret);
        var original = new HttpRequestException($"failed {SecretUrl}", new IOException($"inner {SecretUrl}"));

        var error = HttpErrorMapper.FromException(original, masker);

        Assert.AreNotSame(original, error.Exception);
        Assert.IsInstanceOfType<SanitizedException>(error.Exception);
        Assert.IsFalse(Describe(error).Contains(FakeSecret, StringComparison.Ordinal));
        StringAssert.Contains(error.Exception!.ToString(), "inner", StringComparison.Ordinal);
    }

    [TestMethod]
    public void HttpErrorMapper_SingleArgumentOverload_StillSwapsTheException()
    {
        var original = new InvalidOperationException("odd");

        var error = HttpErrorMapper.FromException(original);

        Assert.IsInstanceOfType<SanitizedException>(error.Exception);
        Assert.AreEqual("odd", error.Message);
    }

    [TestMethod]
    public void SanitizedException_StandardConstructors_Work()
    {
        Assert.IsNull(new SanitizedException().OriginalExceptionType);
        Assert.AreEqual("m", new SanitizedException("m").Message);
        Assert.IsNotNull(new SanitizedException("m", new InvalidOperationException()).InnerException);
        StringAssert.Contains(new SanitizedException("m").ToString(), "m", StringComparison.Ordinal);
    }

    private static string Describe(Error error) =>
        $"{error.Code}|{error.Message}|{string.Join(";", error.Data?.Select(entry => $"{entry.Key}={entry.Value}") ?? [])}|{error.Exception}";

    private static ServiceProvider BuildProvider(FakeTimeProvider clock, CapturingLoggerProvider capture, StubHttpMessageHandler stub)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(capture);
            logging.SetMinimumLevel(LogLevel.Trace);
        });

        services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub)
            .AddOzakboyHttpPipeline(options =>
            {
                options.Signing.ApiKey = ApiKey;
                options.Signing.SecretKey = SecretKey;
                options.EnableRateLimiting = false;
                options.KnownSecrets.Add(FakeSecret);
                options.Logging.LogResponseBody = true;
                options.Retry.Policy = new RetryPolicy
                {
                    MaxAttempts = 2,
                    BaseDelay = TimeSpan.FromMilliseconds(100),
                    JitterRatio = 0d,
                };
                options.Timeouts.AttemptTimeout = TimeSpan.FromMinutes(10);
                options.Timeouts.OverallTimeout = TimeSpan.FromHours(1);
            });

        return services.BuildServiceProvider();
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ozakboy.Core.Abstractions;
using Ozakboy.Security.Masking;

namespace Ozakboy.Http.Logging;

/// <summary>
/// 管線的最內層:記錄實際送出的請求與收到的回應,並在寫入前把憑證遮掉。
/// The innermost handler: logs the request as it actually goes out and the response as it comes back, masking
/// credentials before anything is written.
/// </summary>
/// <remarks>
/// <para>
/// 放在最內層是為了記錄「真正送出去的那一份」:簽章已附上、限流已放行、這是第幾次重試也已確定。
/// 重試在管線最外層,所以每一次嘗試各留一筆。放在外層只會看到呼叫端的意圖,看不到實際發生的事。
/// The innermost position is what makes the log show what actually went out: the signature is attached, rate
/// limiting has admitted it, and which retry this is has been decided. Retry is outermost, so every attempt
/// leaves its own entry. Logging from outside would show the caller's intent rather than events.
/// </para>
/// <para>
/// <b>記錄器永遠拿不到原始例外物件。</b>失敗時交給記錄器的是 <see cref="SanitizedException"/>:
/// 型別名稱、遮罩後的訊息與遮罩後的堆疊都在,但沒有內層例外、也不參照原物件。
/// 0.2.0 把原始例外直接交出去,連線層例外的訊息若帶著位址(路徑裡可能就是憑證),就原封不動寫進日誌。
/// <b>The logger never receives the original exception object.</b> On failure it gets a
/// <see cref="SanitizedException"/>: the type name, masked message and masked stack are all there, but there is
/// no inner exception and no reference to the original. 0.2.0 handed the original over, so a transport exception
/// whose message carried the URI — whose path may be the credential — went into the log untouched.
/// </para>
/// <para>
/// 只相依 <see cref="ILogger"/> 抽象,不綁定任何具體日誌實作。
/// It depends only on the <see cref="ILogger"/> abstraction and binds to no concrete logging implementation.
/// </para>
/// </remarks>
public sealed partial class SanitizingLoggingHandler : DelegatingHandler
{
    private readonly ILogger _logger;
    private readonly RequestLoggingOptions _options;
    private readonly SecretMasker _masker;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 建立處理器。
    /// Creates the handler.
    /// </summary>
    /// <param name="logger">日誌器。The logger.</param>
    /// <param name="options">日誌設定;<see langword="null"/> 時使用預設值。The logging options; defaults are used when <see langword="null"/>.</param>
    /// <param name="timeProvider">時間來源,用於量測耗時。The time source, used to measure elapsed time.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="logger"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定不合法時擲出。Thrown when the options are invalid.
    /// </exception>
    public SanitizingLoggingHandler(ILogger logger, RequestLoggingOptions? options = null, TimeProvider? timeProvider = null)
        : this(logger, options, timeProvider, null)
    {
    }

    /// <summary>
    /// 以指定的遮罩器建立處理器。
    /// Creates the handler with a given masker.
    /// </summary>
    /// <param name="logger">日誌器。The logger.</param>
    /// <param name="options">日誌設定;<see langword="null"/> 時使用預設值。The logging options; defaults are used when <see langword="null"/>.</param>
    /// <param name="timeProvider">時間來源,用於量測耗時。The time source, used to measure elapsed time.</param>
    /// <param name="masker">
    /// 遮罩器;<see langword="null"/> 時依 <paramref name="options"/> 建立一個。傳入時直接使用它,
    /// <see cref="RequestLoggingOptions.AdditionalSensitiveParameterNames"/> 應已反映在建立它的設定裡。
    /// The masker; one is built from <paramref name="options"/> when <see langword="null"/>. When supplied it is
    /// used as is, so <see cref="RequestLoggingOptions.AdditionalSensitiveParameterNames"/> should already be
    /// reflected in whatever built it.
    /// </param>
    /// <remarks>
    /// 傳入共用的遮罩器,是讓執行期登記的祕密不會隨著 <see cref="System.Net.Http.IHttpClientFactory"/>
    /// 重建處理器而消失,也讓日誌與錯誤邊界用同一份祕密清單。
    /// Passing a shared masker keeps secrets registered at run time from vanishing when
    /// <see cref="System.Net.Http.IHttpClientFactory"/> rebuilds handlers, and lets logging and the error boundary
    /// work from one list of secrets.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="logger"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定不合法時擲出。Thrown when the options are invalid.
    /// </exception>
    public SanitizingLoggingHandler(ILogger logger, RequestLoggingOptions? options, TimeProvider? timeProvider, SecretMasker? masker)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var effectiveOptions = options ?? new RequestLoggingOptions();
        var validation = effectiveOptions.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        _logger = logger;
        _options = effectiveOptions;
        _masker = masker ?? effectiveOptions.CreateMasker();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 以選項容器建立處理器,供相依性注入使用。
    /// Creates the handler from an options container, for dependency injection.
    /// </summary>
    /// <param name="loggerFactory">日誌器工廠。The logger factory.</param>
    /// <param name="options">日誌設定容器。The logging options container.</param>
    /// <param name="timeProvider">時間來源。The time source.</param>
    public SanitizingLoggingHandler(ILoggerFactory loggerFactory, IOptions<RequestLoggingOptions> options, TimeProvider? timeProvider = null)
        : this(
            (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<SanitizingLoggingHandler>(),
            (options ?? throw new ArgumentNullException(nameof(options))).Value,
            timeProvider)
    {
    }

    /// <summary>
    /// 登記一個已知的祕密值,之後它出現在任何被記錄的文字裡都會被遮掉。
    /// Registers a known secret so that it is masked wherever it appears in logged text.
    /// </summary>
    /// <param name="secret">祕密值,例如 API 金鑰。The secret, such as an API key.</param>
    /// <returns>登記成功時為 <see langword="true"/>。<see langword="true"/> when the value was registered.</returns>
    /// <remarks>
    /// 參數名比對只擋得住「放在預期位置」的祕密。金鑰被塞進錯誤訊息、回傳的 echo 欄位或自訂標頭時,
    /// 靠名稱是攔不到的;把值本身登記起來才擋得住。
    /// Matching on parameter names only stops secrets that sit where they are expected. A key echoed back in an
    /// error message, or carried in a custom header, slips past name matching entirely; registering the value
    /// itself is what catches it.
    /// </remarks>
    public bool RegisterKnownSecret(string secret) => _masker.RegisterKnownSecret(secret);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var method = request.Method.Method;
        var uri = SafeMasking.MaskUri(_masker, request.RequestUri);
        var start = _timeProvider.GetTimestamp();

        LogRequest(_logger, method, uri);

        // 先問日誌層級再讀內容:讀取與遮罩內容都不便宜,關掉 Debug 時不該付這個代價。
        // Check the level before reading: reading and masking a body are not cheap, and with Debug off that
        // cost should not be paid at all.
        if (_options.LogRequestBody && request.Content is not null && _logger.IsEnabled(LogLevel.Debug))
        {
            var requestBody = await ReadMaskedBodyAsync(request.Content, cancellationToken).ConfigureAwait(false);
            LogRequestBody(_logger, method, uri, requestBody);
        }

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var elapsed = (long)_timeProvider.GetElapsedTime(start).TotalMilliseconds;
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                LogResponse(_logger, method, uri, status, elapsed);
            }
            else
            {
                LogUnsuccessfulResponse(_logger, method, uri, status, elapsed);
            }

            if (_options.LogResponseBody && _logger.IsEnabled(LogLevel.Debug))
            {
                var responseBody = await ReadMaskedBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                LogResponseBody(_logger, method, uri, responseBody);
            }

            return response;
        }
        // 交給記錄器的一律是替身,原始例外照常往外拋 —— 外層的重試處理器要靠它的型別分類,
        // 而它離開管線之前會在錯誤邊界上再被換成替身一次。
        // The logger always gets the stand-in while the original keeps propagating: the retry handler outside
        // classifies by its type, and it is swapped for a stand-in again at the error boundary before leaving.
        catch (ResultException exception)
        {
            LogRequestFailed(_logger, method, uri, (long)_timeProvider.GetElapsedTime(start).TotalMilliseconds, ErrorSanitizer.Sanitize(exception, _masker));
            throw;
        }
        catch (HttpRequestException exception)
        {
            LogRequestFailed(_logger, method, uri, (long)_timeProvider.GetElapsedTime(start).TotalMilliseconds, ErrorSanitizer.Sanitize(exception, _masker));
            throw;
        }
        catch (OperationCanceledException exception)
        {
            LogRequestFailed(_logger, method, uri, (long)_timeProvider.GetElapsedTime(start).TotalMilliseconds, ErrorSanitizer.Sanitize(exception, _masker));
            throw;
        }
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "HTTP 送出 {Method} {Uri}")]
    private static partial void LogRequest(ILogger logger, string method, string uri);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "HTTP 請求內容 {Method} {Uri}:{Body}")]
    private static partial void LogRequestBody(ILogger logger, string method, string uri, string body);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "HTTP 回應 {Method} {Uri} -> {StatusCode},耗時 {ElapsedMilliseconds} ms")]
    private static partial void LogResponse(ILogger logger, string method, string uri, int statusCode, long elapsedMilliseconds);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "HTTP 回應非成功 {Method} {Uri} -> {StatusCode},耗時 {ElapsedMilliseconds} ms")]
    private static partial void LogUnsuccessfulResponse(ILogger logger, string method, string uri, int statusCode, long elapsedMilliseconds);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Debug, Message = "HTTP 回應內容 {Method} {Uri}:{Body}")]
    private static partial void LogResponseBody(ILogger logger, string method, string uri, string body);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Error, Message = "HTTP 請求失敗 {Method} {Uri},耗時 {ElapsedMilliseconds} ms")]
    private static partial void LogRequestFailed(ILogger logger, string method, string uri, long elapsedMilliseconds, Exception exception);

    private async Task<string> ReadMaskedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            var body = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return SafeMasking.MaskBody(_masker, body, _options.MaxBodyLength);
        }
        catch (HttpRequestException)
        {
            return SafeMasking.MaskingFailedPlaceholder;
        }
        catch (ObjectDisposedException)
        {
            return SafeMasking.MaskingFailedPlaceholder;
        }
        catch (InvalidOperationException)
        {
            return SafeMasking.MaskingFailedPlaceholder;
        }
    }
}

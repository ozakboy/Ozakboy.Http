using Microsoft.Extensions.Logging;
using Ozakboy.Core.Abstractions;
using Ozakboy.Security.Masking;

namespace Ozakboy.Http.Logging;

/// <summary>
/// 脫敏日誌處理器的設定。
/// Options for the sanitising logging handler.
/// </summary>
/// <remarks>
/// 敏感參數名的預設清單來自 <see cref="SecretMaskOptions.DefaultSensitiveNames"/>,已涵蓋
/// <c>apiKey</c>、<c>signature</c>、<c>token</c>、<c>authorization</c> 等常見名稱,比對一律忽略大小寫。
/// 這裡只需要補上對接對象特有的名稱。
/// The default sensitive-name list comes from <see cref="SecretMaskOptions.DefaultSensitiveNames"/>, which
/// already covers <c>apiKey</c>, <c>signature</c>, <c>token</c>, <c>authorization</c> and friends, matched
/// case-insensitively. Only names specific to the service being called need adding here.
/// </remarks>
public sealed class RequestLoggingOptions
{
    /// <summary>
    /// 除預設清單外,額外要視為敏感的參數名。
    /// Additional parameter names to treat as sensitive, on top of the default list.
    /// </summary>
    public IList<string> AdditionalSensitiveParameterNames { get; } = [];

    /// <summary>
    /// 是否記錄請求內容。
    /// Whether to log the request body.
    /// </summary>
    /// <remarks>
    /// 預設關閉。請求內容常含下單參數,量大且多半只在診斷特定問題時才需要。
    /// Off by default. Request bodies carry order parameters, are voluminous, and are usually wanted only
    /// while diagnosing a specific problem.
    /// </remarks>
    public bool LogRequestBody { get; set; }

    /// <summary>
    /// 是否記錄回應內容。
    /// Whether to log the response body.
    /// </summary>
    public bool LogResponseBody { get; set; }

    /// <summary>
    /// 記錄內容時保留的最大字元數,超過的部分截斷。
    /// The maximum number of characters kept when logging a body; anything longer is truncated.
    /// </summary>
    public int MaxBodyLength { get; set; } = 2048;

    /// <summary>
    /// 非成功回應(事件 1003)的一般記錄等級:不屬於升級對象的非成功狀態碼都用這個等級。預設 <see cref="LogLevel.Information"/>。
    /// The level for unsuccessful responses (event 1003) in general: every unsuccessful status code that is not
    /// elevated uses it. Defaults to <see cref="LogLevel.Information"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 0.3.3 以前固定是 <see cref="LogLevel.Warning"/>。但對方回 4xx 多半是業務上預期得到的結果(查一張已經撤掉的單、
    /// 被拒絕的只減倉單),全部記成警告只會淹沒真正該看的那幾筆;官方的 <c>LoggingHttpMessageHandler</c>
    /// 也不論狀態碼一律記 Information。要回到舊行為,把這裡設成 <see cref="LogLevel.Warning"/> 即可。
    /// Up to 0.3.3 this was fixed at <see cref="LogLevel.Warning"/>. A 4xx from the peer is usually an expected
    /// business outcome — querying an order already cancelled, a reduce-only order refused — and logging every one
    /// as a warning buries the few that matter; the official <c>LoggingHttpMessageHandler</c> logs at Information
    /// whatever the status code. Set this to <see cref="LogLevel.Warning"/> to get the old behaviour back.
    /// </para>
    /// <para>
    /// <see cref="LogLevel.None"/> 表示不記錄。成功回應不受這項設定影響,仍以 <see cref="LogLevel.Debug"/> 記錄(事件 1002)。
    /// <see cref="LogLevel.None"/> means not logged. Successful responses are unaffected and stay at
    /// <see cref="LogLevel.Debug"/> (event 1002).
    /// </para>
    /// </remarks>
    public LogLevel UnsuccessfulResponseLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// 升級的非成功回應所用的記錄等級:5xx(<see cref="ElevateServerErrors"/> 開啟時)與
    /// <see cref="ElevatedStatusCodes"/> 裡的狀態碼。預設 <see cref="LogLevel.Warning"/>。
    /// The level for elevated unsuccessful responses: 5xx (while <see cref="ElevateServerErrors"/> is on) and the
    /// status codes in <see cref="ElevatedStatusCodes"/>. Defaults to <see cref="LogLevel.Warning"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="LogLevel.None"/> 表示不記錄。
    /// <see cref="LogLevel.None"/> means not logged.
    /// </remarks>
    public LogLevel ElevatedResponseLevel { get; set; } = LogLevel.Warning;

    /// <summary>
    /// 是否把所有 5xx 回應升級到 <see cref="ElevatedResponseLevel"/>。預設開啟。
    /// Whether every 5xx response is elevated to <see cref="ElevatedResponseLevel"/>. On by default.
    /// </summary>
    /// <remarks>
    /// 5xx 代表對方自己出了狀況,不是請求本身寫錯,值得被看見。
    /// A 5xx means the peer itself is in trouble rather than the request being wrong, and deserves to be seen.
    /// </remarks>
    public bool ElevateServerErrors { get; set; } = true;

    /// <summary>
    /// 另外要升級到 <see cref="ElevatedResponseLevel"/> 的狀態碼。預設為 429 與 418。
    /// Additional status codes elevated to <see cref="ElevatedResponseLevel"/>. Defaults to 429 and 418.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 429 是限流,418 是部分服務在限流被無視之後改回的封鎖碼;兩者都表示用戶端的送出節奏出了問題,必須被看見。
    /// 名單可增可刪,例如 <c>ElevatedStatusCodes.Clear()</c> 之後只剩 5xx 會升級。
    /// 429 is rate limiting, and 418 is what some services answer with once a rate limit has been ignored and they
    /// ban the caller; either means the client's pacing has gone wrong and must be seen. The set can be added to or
    /// trimmed — after <c>ElevatedStatusCodes.Clear()</c>, only 5xx is elevated.
    /// </para>
    /// <para>
    /// 只有非成功回應會查這份名單,放進 2xx 不會有任何效果。每個值必須介於 100 到 999。
    /// 處理器在建構當下複製一份,之後再改這份名單不影響已建立的處理器。
    /// Only unsuccessful responses consult the set, so a 2xx in it has no effect. Every value must lie between 100
    /// and 999. A handler copies the set when it is constructed; changing the set afterwards does not affect a
    /// handler already built.
    /// </para>
    /// </remarks>
    public ISet<int> ElevatedStatusCodes { get; } = new HashSet<int> { 418, 429 };

    /// <summary>
    /// 檢查設定是否可用。
    /// Validates the options.
    /// </summary>
    /// <returns>
    /// 設定可用時為成功;否則為帶 <see cref="ErrorCategory.Validation"/> 的失敗。
    /// Success when the options are usable, otherwise a failure carrying <see cref="ErrorCategory.Validation"/>.
    /// </returns>
    public Result Validate()
    {
        if (MaxBodyLength < 1)
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "內容記錄長度必須為正整數。The body log length must be positive.");
        }

        if (AdditionalSensitiveParameterNames.Any(string.IsNullOrWhiteSpace))
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "額外的敏感參數名不可為空白。Additional sensitive parameter names must not be blank.");
        }

        if (!Enum.IsDefined(UnsuccessfulResponseLevel) || !Enum.IsDefined(ElevatedResponseLevel))
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "非成功回應的記錄等級必須是已定義的 LogLevel。Unsuccessful-response log levels must be defined LogLevel values.");
        }

        if (ElevatedStatusCodes.Any(code => code is < 100 or > 999))
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "升級的狀態碼必須介於 100 到 999。Elevated status codes must lie between 100 and 999.");
        }

        return Result.Success();
    }

    /// <summary>
    /// 依這份設定建立遮罩器。
    /// Builds the masker described by these options.
    /// </summary>
    /// <returns>遮罩器。The masker.</returns>
    public SecretMasker CreateMasker() => new(new SecretMaskOptions
    {
        AdditionalSensitiveNames = [.. AdditionalSensitiveParameterNames],
    });
}

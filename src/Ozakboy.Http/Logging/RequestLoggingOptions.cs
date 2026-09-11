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

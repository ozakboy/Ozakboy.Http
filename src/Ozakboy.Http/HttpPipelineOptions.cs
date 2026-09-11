using Ozakboy.Core.Abstractions;
using Ozakboy.Http.Logging;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;

namespace Ozakboy.Http;

/// <summary>
/// 整條管線的設定,供一次註冊四個處理器時使用。
/// The configuration for the whole pipeline, used when registering all four handlers at once.
/// </summary>
/// <remarks>
/// 簽章與限流兩段可以各自關掉(<see cref="EnableSigning"/>、<see cref="EnableRateLimiting"/>),
/// 因為公開端點不需要簽章,而某些對接對象沒有配額制。重試與日誌則一律啟用 ——
/// 關掉日誌等於放棄事後追查,關掉重試則讓暫時性故障直接冒到交易邏輯。
/// Signing and rate limiting can each be switched off (<see cref="EnableSigning"/>,
/// <see cref="EnableRateLimiting"/>) because public endpoints need no signature and some peers enforce no
/// quota. Retry and logging are always on: turning logging off forfeits any post-mortem, and turning retry off
/// pushes transient faults straight into the trading logic.
/// </remarks>
public sealed class HttpPipelineOptions
{
    /// <summary>
    /// 是否加入簽章處理器。
    /// Whether to add the signing handler.
    /// </summary>
    public bool EnableSigning { get; set; } = true;

    /// <summary>
    /// 是否加入限流處理器。
    /// Whether to add the rate-limiting handler.
    /// </summary>
    public bool EnableRateLimiting { get; set; } = true;

    /// <summary>
    /// 簽章設定。
    /// The signing options.
    /// </summary>
    public SigningOptions Signing { get; } = new();

    /// <summary>
    /// 限流設定。
    /// The rate-limit options.
    /// </summary>
    public RateLimitOptions RateLimiting { get; } = new();

    /// <summary>
    /// 重試設定。
    /// The retry options.
    /// </summary>
    public RetryOptions Retry { get; } = new();

    /// <summary>
    /// 逾時設定。
    /// The timeout options.
    /// </summary>
    public HttpTimeoutOptions Timeouts { get; } = new();

    /// <summary>
    /// 日誌設定。
    /// The logging options.
    /// </summary>
    public RequestLoggingOptions Logging { get; } = new();

    /// <summary>
    /// 除了簽章金鑰之外,這個用戶端另外要當成祕密的字面值,例如放在請求路徑裡的權杖。
    /// Literal values this client should treat as secrets in addition to the signing keys, such as a token that
    /// travels in the request path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 欄位名規則只看得到 query 參數與 JSON 欄位的名稱;路徑的一段沒有名字,例外訊息與錯誤資料也沒有,
    /// 在那些地方攔得住祕密的只有「已登記值的字面替換」。<see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/>
    /// 會把這裡的每一個值、連同 <see cref="SigningOptions.ApiKey"/> 與 <see cref="SigningOptions.SecretKey"/>,
    /// 登記到這個用戶端的遮罩器。執行期才取得的祕密,請改用
    /// <see cref="OzakboyHttpServiceProviderExtensions.GetOzakboyHttpMasker"/> 取得同一個遮罩器再登記。
    /// Name rules only see query-parameter and JSON field names. A path segment has no name, and neither do
    /// exception messages or error data; there, only literal replacement of registered values catches a secret.
    /// <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/> registers every value here, along
    /// with <see cref="SigningOptions.ApiKey"/> and <see cref="SigningOptions.SecretKey"/>, on this client's
    /// masker. For secrets obtained at run time, get that same masker through
    /// <see cref="OzakboyHttpServiceProviderExtensions.GetOzakboyHttpMasker"/> and register them there.
    /// </para>
    /// <para>
    /// 每個值至少 <see cref="Ozakboy.Security.Masking.SecretMasker.MinimumKnownSecretLength"/> 個字元;
    /// 更短的值會讓一般日誌內容被大量誤遮,因此在註冊時就被拒絕。
    /// Each value must be at least <see cref="Ozakboy.Security.Masking.SecretMasker.MinimumKnownSecretLength"/>
    /// characters; anything shorter would mask swathes of ordinary log text and is rejected at registration.
    /// </para>
    /// </remarks>
    public IList<string> KnownSecrets { get; } = [];

    /// <summary>
    /// 檢查所有啟用中的段落設定是否可用。
    /// Validates every enabled section.
    /// </summary>
    /// <returns>
    /// 全部可用時為成功;否則為第一個不可用段落的失敗。
    /// Success when all are usable, otherwise the failure from the first section that is not.
    /// </returns>
    public Result Validate()
    {
        if (EnableSigning)
        {
            var signing = Signing.Validate();
            if (signing.IsFailure)
            {
                return signing;
            }
        }

        if (EnableRateLimiting)
        {
            var rateLimiting = RateLimiting.Validate();
            if (rateLimiting.IsFailure)
            {
                return rateLimiting;
            }
        }

        if (KnownSecrets.Any(secret =>
                string.IsNullOrWhiteSpace(secret) || secret.Length < Ozakboy.Security.Masking.SecretMasker.MinimumKnownSecretLength))
        {
            // 訊息只講規則,絕不回述被拒絕的值 —— 這個例外多半會被寫進日誌。
            // The message states the rule and never echoes the rejected value: this exception is likely to be logged.
            return Error.Validation(
                HttpErrorCodes.InvalidOptions,
                $"已知祕密不可為空白,且長度至少 {Ozakboy.Security.Masking.SecretMasker.MinimumKnownSecretLength} 個字元。Known secrets must not be blank and must be at least {Ozakboy.Security.Masking.SecretMasker.MinimumKnownSecretLength} characters long.");
        }

        return Retry.Validate()
            .Then(Timeouts.Validate)
            .Then(Logging.Validate);
    }
}

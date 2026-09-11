using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http;

/// <summary>
/// 兩層逾時設定:單次嘗試與整趟請求各一個上限。
/// Two layers of timeout: one bound for a single attempt and one for the whole exchange.
/// </summary>
/// <remarks>
/// <para>
/// 只有單層逾時是不夠的。只設整體逾時,一次卡住的嘗試會把整個預算吃光,重試永遠輪不到;
/// 只設單次逾時,則「每次都剛好在逾時前回一個 503」的對手可以讓呼叫端等上任意久。
/// One layer is not enough. With only an overall bound, a single stuck attempt eats the entire budget and the
/// retries never get their turn; with only a per-attempt bound, a peer that answers 503 just before each
/// deadline can keep the caller waiting indefinitely.
/// </para>
/// <para>
/// 整體逾時由 <see cref="HttpPipelineClient"/> 施加,單次嘗試逾時由重試處理器施加。
/// <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/> 會把 <see cref="HttpClient.Timeout"/>
/// 設為無限,讓逾時完全交給這兩層;不走 <see cref="HttpPipelineClient"/> 的呼叫端,整體上限請以自己的取消權杖施加。
/// The overall bound is applied by <see cref="HttpPipelineClient"/> and the per-attempt bound by the retry
/// handler. <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/> sets
/// <see cref="HttpClient.Timeout"/> to infinite so these two layers own timing entirely; callers who bypass
/// <see cref="HttpPipelineClient"/> should impose the overall bound with their own cancellation token.
/// </para>
/// </remarks>
public sealed class HttpTimeoutOptions
{
    /// <summary>
    /// 單次嘗試的上限。逾時視為暫時性失敗,仍可能觸發重試。
    /// The bound for a single attempt. A timeout counts as transient and may trigger a retry.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 整趟請求(含所有重試與退避等待)的上限。超過就直接放棄。
    /// The bound for the whole exchange, retries and backoff waits included. Past it the client gives up.
    /// </summary>
    public TimeSpan OverallTimeout { get; set; } = TimeSpan.FromSeconds(30);

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
        if (AttemptTimeout <= TimeSpan.Zero)
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "單次嘗試逾時必須為正值。The attempt timeout must be positive.");
        }

        if (OverallTimeout <= TimeSpan.Zero)
        {
            return Error.Validation(HttpErrorCodes.InvalidOptions, "整體逾時必須為正值。The overall timeout must be positive.");
        }

        if (OverallTimeout < AttemptTimeout)
        {
            return Error.Validation(
                HttpErrorCodes.InvalidOptions,
                "整體逾時不可短於單次嘗試逾時,否則第一次嘗試就註定跑不完。The overall timeout must not be shorter than the attempt timeout, or the first attempt can never finish.");
        }

        return Result.Success();
    }
}

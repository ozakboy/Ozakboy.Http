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

        return Retry.Validate()
            .Then(Timeouts.Validate)
            .Then(Logging.Validate);
    }
}

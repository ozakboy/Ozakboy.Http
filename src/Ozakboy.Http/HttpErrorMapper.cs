using System.Globalization;
using System.Net;
using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http;

/// <summary>
/// 把 HTTP 狀態碼與傳輸層例外對映成 <see cref="Error"/>,並判斷是否為暫時性。
/// Maps HTTP status codes and transport exceptions onto <see cref="Error"/>, including whether the failure is
/// transient.
/// </summary>
/// <remarks>
/// 「是否暫時性」由 <see cref="ErrorCategory"/> 決定,而不是在這裡另開一套布林判斷 ——
/// 重試處理器問的是 <see cref="Error.IsTransient"/>,兩邊必須是同一個真相來源。
/// Whether a failure is transient is decided by its <see cref="ErrorCategory"/> rather than by a separate
/// boolean here: the retry handler asks <see cref="Error.IsTransient"/>, and both must read the same truth.
/// </remarks>
public static class HttpErrorMapper
{
    /// <summary>
    /// 把 HTTP 狀態碼對映成錯誤。
    /// Maps an HTTP status code onto an error.
    /// </summary>
    /// <param name="statusCode">狀態碼。The status code.</param>
    /// <param name="reasonPhrase">回應的原因片語,可為 <see langword="null"/>。The reason phrase, if any.</param>
    /// <param name="bodySnippet">回應內容摘要,可為 <see langword="null"/>。A snippet of the response body, if any.</param>
    /// <returns>對應的錯誤。The mapped error.</returns>
    /// <remarks>
    /// 分類原則:429 為 <see cref="ErrorCategory.RateLimited"/>、408 為 <see cref="ErrorCategory.Timeout"/>、
    /// 所有 5xx 為 <see cref="ErrorCategory.Unavailable"/> —— 這三類是暫時性的,重試有意義。
    /// 其餘 4xx 不是暫時性的:請求本身有問題,原封不動重送只會得到同一個答案,還多消耗一次配額。
    /// The rules: 429 becomes <see cref="ErrorCategory.RateLimited"/>, 408 becomes
    /// <see cref="ErrorCategory.Timeout"/>, and every 5xx becomes <see cref="ErrorCategory.Unavailable"/> —
    /// the three transient categories, where retrying means something. Other 4xx codes are not transient: the
    /// request itself is wrong, and resending it unchanged earns the same answer plus another slice of quota.
    /// </remarks>
    public static Error FromStatusCode(HttpStatusCode statusCode, string? reasonPhrase = null, string? bodySnippet = null)
    {
        var code = (int)statusCode;
        var category = code switch
        {
            429 => ErrorCategory.RateLimited,
            408 => ErrorCategory.Timeout,
            401 => ErrorCategory.Unauthorized,
            403 => ErrorCategory.Forbidden,
            404 => ErrorCategory.NotFound,
            409 => ErrorCategory.Conflict,
            >= 500 and <= 599 => ErrorCategory.Unavailable,
            >= 400 and <= 499 => ErrorCategory.Validation,
            _ => ErrorCategory.Unexpected,
        };

        var message = string.IsNullOrWhiteSpace(reasonPhrase)
            ? string.Create(CultureInfo.InvariantCulture, $"HTTP {code}。HTTP {code}.")
            : string.Create(CultureInfo.InvariantCulture, $"HTTP {code} {reasonPhrase}");

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["statusCode"] = code.ToString(CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(bodySnippet))
        {
            data["body"] = bodySnippet;
        }

        return new Error(HttpErrorCodes.ForStatus(statusCode), message, category)
        {
            Data = data,
        };
    }

    /// <summary>
    /// 把傳輸層例外對映成錯誤。
    /// Maps a transport-level exception onto an error.
    /// </summary>
    /// <param name="exception">例外。The exception.</param>
    /// <returns>對應的錯誤。The mapped error.</returns>
    /// <remarks>
    /// <see cref="OperationCanceledException"/> 同時代表「呼叫端取消」與「逾時」兩件完全不同的事,
    /// 光看例外型別分不出來。這裡一律歸為 <see cref="ErrorCategory.Cancelled"/>(不可重試);
    /// 知道自己是逾時的呼叫端(例如握有逾時權杖的 <see cref="HttpPipelineClient"/>)應自行改用逾時錯誤。
    /// An <see cref="OperationCanceledException"/> means two completely different things — the caller cancelled,
    /// or something timed out — and the exception type alone cannot tell them apart. It is mapped here to
    /// <see cref="ErrorCategory.Cancelled"/>, which is not retryable; a caller that knows it was a timeout
    /// (<see cref="HttpPipelineClient"/>, which holds the timeout token) substitutes the timeout error itself.
    /// </remarks>
    public static Error FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            TimeoutException => new Error(HttpErrorCodes.Timeout, "請求逾時。The request timed out.", ErrorCategory.Timeout)
            {
                Exception = exception,
            },
            OperationCanceledException => new Error(HttpErrorCodes.Cancelled, "請求已取消。The request was cancelled.", ErrorCategory.Cancelled)
            {
                Exception = exception,
            },
            HttpRequestException => new Error(HttpErrorCodes.Network, $"連線失敗:{exception.Message}", ErrorCategory.Network)
            {
                Exception = exception,
            },
            _ => Error.FromException(exception, HttpErrorCodes.Network, ErrorCategory.Network),
        };
    }

    /// <summary>
    /// 讀取回應內容摘要並對映成錯誤。
    /// Reads a snippet of the response body and maps the response onto an error.
    /// </summary>
    /// <param name="response">回應。The response.</param>
    /// <param name="maxBodyLength">摘要的最大長度。The maximum snippet length.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>對應的錯誤。The mapped error.</returns>
    /// <remarks>
    /// 內容讀取失敗不會讓這個方法失敗 —— 診斷資訊少一點,好過在錯誤處理路徑上再拋一個例外,
    /// 那會把真正的失敗原因蓋掉。
    /// A failure while reading the body does not fail this method: less diagnostic detail is better than
    /// throwing a second exception on the error path, which would bury the real cause.
    /// </remarks>
    public static async Task<Error> FromResponseAsync(
        HttpResponseMessage response,
        int maxBodyLength = 512,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBodyLength);

        string? snippet = null;
        if (maxBodyLength > 0)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                snippet = body.Length > maxBodyLength ? body[..maxBodyLength] : body;
            }
            catch (HttpRequestException)
            {
                snippet = null;
            }
            catch (OperationCanceledException)
            {
                snippet = null;
            }
        }

        return FromStatusCode(response.StatusCode, response.ReasonPhrase, snippet);
    }

    /// <summary>
    /// 取出回應的 <c>Retry-After</c> 指示。
    /// Reads the response's <c>Retry-After</c> instruction.
    /// </summary>
    /// <param name="response">回應。The response.</param>
    /// <param name="timeProvider">時間來源,用於把 HTTP 日期換算成等待長度。The time source, used to turn an HTTP date into a delay.</param>
    /// <param name="delay">等待長度。The delay.</param>
    /// <returns>有指示時為 <see langword="true"/>。<see langword="true"/> when the header is present and usable.</returns>
    /// <remarks>
    /// 對方說要等多久就等多久,不要拿自己算的退避間隔覆蓋它。伺服器知道自己的封鎖窗口還剩多長,
    /// 提早重送只會把封鎖時間延長。
    /// Wait as long as the peer says, and do not override it with a locally computed backoff. The server knows
    /// how much of its cooldown remains; coming back early only extends the ban.
    /// </remarks>
    public static bool TryGetRetryAfter(HttpResponseMessage response, TimeProvider timeProvider, out TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(timeProvider);

        delay = TimeSpan.Zero;

        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return false;
        }

        if (retryAfter.Delta is { } delta)
        {
            delay = delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            return true;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - timeProvider.GetUtcNow();
            delay = wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            return true;
        }

        return false;
    }
}

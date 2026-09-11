using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http;

/// <summary>
/// 管線內部用來把 <see cref="Ozakboy.Core.Abstractions.Error"/> 送出處理器邊界的例外。
/// The exception the pipeline uses to carry an <see cref="Ozakboy.Core.Abstractions.Error"/> across a handler boundary.
/// </summary>
/// <remarks>
/// <para>
/// 本套件的對外契約是「預期失敗回傳 <see cref="Result{T}"/>,不用例外表達」,但
/// <see cref="DelegatingHandler.SendAsync"/> 的簽章由 BCL 決定,只能回 <see cref="HttpResponseMessage"/>。
/// 簽章金鑰缺漏、本地限流排不到額度這類「請求根本沒送出」的失敗無法塞進回應物件,只能沿著例外路徑往上拋。
/// This package's outward contract is that expected failures come back as <see cref="Result{T}"/> rather than
/// as exceptions, but the signature of <see cref="DelegatingHandler.SendAsync"/> belongs to the BCL and can
/// only return an <see cref="HttpResponseMessage"/>. Failures where the request was never sent at all — a
/// missing signing secret, or local rate limiting giving up — have nowhere to live inside a response object,
/// so they travel up the exception path instead.
/// </para>
/// <para>
/// <see cref="HttpPipelineClient"/> 會在邊界把它接住並轉回 <see cref="Result{T}"/>。
/// 直接使用 <see cref="HttpClient"/> 的呼叫端則需要自行攔截這個型別。
/// <see cref="HttpPipelineClient"/> catches it at the boundary and converts it back into a
/// <see cref="Result{T}"/>. Callers using a bare <see cref="HttpClient"/> need to catch this type themselves.
/// </para>
/// </remarks>
public sealed class HttpPipelineException : Exception
{
    /// <summary>
    /// 以既有錯誤建立例外。
    /// Creates the exception from an existing error.
    /// </summary>
    /// <param name="error">要攜帶的錯誤。The error to carry.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="error"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="error"/> is <see langword="null"/>.
    /// </exception>
    public HttpPipelineException(Error error)
        : base(GetMessage(error), error?.Exception)
    {
        Error = error!;
    }

    /// <summary>
    /// 建立例外(標準建構式,供序列化與框架使用)。
    /// Creates the exception with a default error (standard constructor, for framework use).
    /// </summary>
    public HttpPipelineException()
        : this(Error.Internal(HttpErrorCodes.InvalidOptions, "HTTP 管線失敗。The HTTP pipeline failed."))
    {
    }

    /// <summary>
    /// 以訊息建立例外(標準建構式)。
    /// Creates the exception from a message (standard constructor).
    /// </summary>
    /// <param name="message">錯誤訊息。The error message.</param>
    public HttpPipelineException(string message)
        : this(Error.Internal(HttpErrorCodes.InvalidOptions, string.IsNullOrWhiteSpace(message) ? "HTTP 管線失敗。The HTTP pipeline failed." : message))
    {
    }

    /// <summary>
    /// 以訊息與內部例外建立例外(標準建構式)。
    /// Creates the exception from a message and an inner exception (standard constructor).
    /// </summary>
    /// <param name="message">錯誤訊息。The error message.</param>
    /// <param name="innerException">內部例外。The inner exception.</param>
    public HttpPipelineException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = Error.FromException(
            innerException ?? new InvalidOperationException(message),
            HttpErrorCodes.InvalidOptions,
            ErrorCategory.Internal);
    }

    /// <summary>
    /// 例外所攜帶的錯誤。保證非 <see langword="null"/>。
    /// The error carried by this exception. Never <see langword="null"/>.
    /// </summary>
    public Error Error { get; }

    private static string GetMessage(Error? error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.ToString();
    }
}

namespace Ozakboy.Http;

/// <summary>
/// 取代原始例外、已遮罩祕密的替身。本套件交給日誌器的例外,以及本套件產生的 <c>Error.Exception</c>,
/// 只會是這個型別或 <see langword="null"/>。
/// A masked stand-in for an original exception. Every exception this package hands to a logger, and every
/// <c>Error.Exception</c> this package produces, is either this type or <see langword="null"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼不能把原始例外交出去。</b>連線層例外的訊息常帶著請求位址,而有些服務把憑證放在位址的路徑裡
/// (例如 Telegram 的 <c>/bot&lt;token&gt;/</c>);內層例外、自訂例外的屬性、<see cref="Exception.Data"/>,
/// 日誌框架都可能結構化輸出。字串可以遮罩,例外物件卻不行 —— <see cref="Exception.Message"/> 是唯讀的,
/// 內層例外鏈也改不動。只要原始物件被交出去,已登記祕密的字面替換就對它完全無效。
/// <b>Why the original cannot be handed over.</b> A transport exception's message often carries the request
/// URI, and some services put the credential in the URI's path (Telegram's <c>/bot&lt;token&gt;/</c>, for one);
/// inner exceptions, custom exception properties and <see cref="Exception.Data"/> may all be rendered
/// structurally by a logging framework. Strings can be masked; an exception object cannot —
/// <see cref="Exception.Message"/> is read-only and the inner-exception chain cannot be rewritten. Once the
/// original object is handed over, literal replacement of registered secrets has no effect on it at all.
/// </para>
/// <para>
/// 因此一律換成這個替身:原始型別名稱、遮罩後的訊息,以及遮罩後的完整 <c>ToString()</c>(含內層例外與堆疊)
/// 都保留下來供診斷,但它沒有內層例外,也不持有原始物件的任何參照。代價是堆疊追蹤變成文字而不是物件;
/// 程式要分支的是 <c>Error.Code</c> 與 <c>Error.Category</c>,不是例外型別,這點損失可以接受。
/// So it is always swapped for this stand-in: the original type name, the masked message and the masked full
/// <c>ToString()</c> (inner exceptions and stack included) are kept for diagnosis, but it has no inner
/// exception and holds no reference to the original object. The cost is a stack trace that is text rather
/// than an object; code branches on <c>Error.Code</c> and <c>Error.Category</c>, not on exception types, so the
/// loss is acceptable.
/// </para>
/// </remarks>
public sealed class SanitizedException : Exception
{
    /// <summary>
    /// 建立替身。
    /// Creates the stand-in.
    /// </summary>
    public SanitizedException()
    {
    }

    /// <summary>
    /// 以訊息建立替身。
    /// Creates the stand-in with a message.
    /// </summary>
    /// <param name="message">訊息。The message.</param>
    public SanitizedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 以訊息與內層例外建立替身。本套件自己從不使用這個建構式 —— 帶內層例外就違背了這個型別存在的理由;
    /// 它存在只是為了滿足例外型別的標準形狀。
    /// Creates the stand-in with a message and an inner exception. This package never uses this constructor
    /// itself — carrying an inner exception defeats the purpose of the type; it exists only to satisfy the
    /// standard shape expected of an exception type.
    /// </summary>
    /// <param name="message">訊息。The message.</param>
    /// <param name="innerException">內層例外。The inner exception.</param>
    public SanitizedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal SanitizedException(string originalExceptionType, string sanitizedMessage, string sanitizedDetails)
        : base(sanitizedMessage)
    {
        OriginalExceptionType = originalExceptionType;
        SanitizedDetails = sanitizedDetails;
    }

    /// <summary>
    /// 原始例外的完整型別名稱,例如 <c>System.Net.Http.HttpRequestException</c>。
    /// The original exception's full type name, such as <c>System.Net.Http.HttpRequestException</c>.
    /// </summary>
    public string? OriginalExceptionType { get; }

    /// <summary>
    /// 原始例外遮罩後的完整文字(含內層例外與堆疊追蹤)。
    /// The original exception's full text, masked, inner exceptions and stack trace included.
    /// </summary>
    public string? SanitizedDetails { get; }

    /// <summary>
    /// 回傳包含遮罩後原始細節的文字,讓日誌框架照常呼叫 <c>ToString()</c> 時仍拿得到診斷資訊。
    /// Returns text that includes the masked original details, so a logging framework calling
    /// <c>ToString()</c> as usual still gets the diagnostics.
    /// </summary>
    /// <returns>例外的文字表示。The exception's text representation.</returns>
    public override string ToString() =>
        SanitizedDetails is null
            ? base.ToString()
            : $"{GetType().FullName}: {Message}{Environment.NewLine}--- 原始例外(已遮罩) Original exception (masked): {OriginalExceptionType} ---{Environment.NewLine}{SanitizedDetails}";
}

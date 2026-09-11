using Ozakboy.Core.Abstractions;
using Ozakboy.Http.Logging;
using Ozakboy.Security.Masking;

namespace Ozakboy.Http;

/// <summary>
/// 把祕密從要交出去的錯誤與例外上遮掉:代碼、訊息、每一筆資料,以及例外物件本身。
/// Masks secrets out of errors and exceptions about to leave: the code, the message, every data entry, and
/// the exception object itself.
/// </summary>
/// <remarks>
/// <para>
/// 這是錯誤離開本套件前的唯一關口。<see cref="Error.Message"/> 與 <see cref="Error.Data"/> 是字串,
/// 經已登記祕密的字面替換即可;<see cref="Error.Exception"/> 是物件,只能整個換成 <see cref="SanitizedException"/>。
/// This is the single checkpoint errors pass before leaving the package. <see cref="Error.Message"/> and
/// <see cref="Error.Data"/> are strings, so literal replacement of registered secrets suffices;
/// <see cref="Error.Exception"/> is an object, so the only option is to swap it wholesale for a
/// <see cref="SanitizedException"/>.
/// </para>
/// <para>
/// 例外一律替換,不論原始例外看起來乾不乾淨。「看起來乾淨就保留原物件」把安全建立在
/// 「現在的檢查涵蓋了例外物件的所有欄位」上,而內層例外的 <see cref="Exception.Data"/>、自訂例外的屬性
/// 都不在任何檢查的視野裡。一律替換才是失敗封閉。
/// The exception is always replaced, however clean the original looks. Keeping it when it looks clean rests
/// safety on the check covering every field of the object, and an inner exception's
/// <see cref="Exception.Data"/> or a custom exception's properties are outside any such check. Always
/// replacing is what fails closed.
/// </para>
/// </remarks>
internal static class ErrorSanitizer
{
    /// <summary>
    /// 回傳遮罩後的新錯誤。分類不變。
    /// Returns a masked copy of the error. The category is unchanged.
    /// </summary>
    /// <param name="error">來源錯誤。The source error.</param>
    /// <param name="masker">遮罩器。The masker.</param>
    /// <returns>可安全交出去的錯誤。An error safe to hand over.</returns>
    public static Error Sanitize(Error error, SecretMasker masker)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(masker);

        return new Error(MaskNonBlank(masker, error.Code), MaskNonBlank(masker, error.Message), error.Category)
        {
            Exception = error.Exception is null ? null : Sanitize(error.Exception, masker),
            Data = SanitizeData(error.Data, masker),
        };
    }

    /// <summary>
    /// 把例外換成遮罩後的替身。
    /// Swaps an exception for its masked stand-in.
    /// </summary>
    /// <param name="exception">來源例外。The source exception.</param>
    /// <param name="masker">遮罩器。The masker.</param>
    /// <returns>替身。The stand-in.</returns>
    /// <remarks>
    /// 已經是替身的例外也會以這一次的遮罩器重新遮一遍,而不是原樣放行:較內層的元件可能只拿得到預設遮罩器,
    /// 邊界這裡拿到的是登記了這個用戶端祕密的那一個。
    /// A stand-in is re-masked with this masker rather than passed straight through: an inner component may
    /// only have had the default masker, while the boundary here holds the one carrying this client's secrets.
    /// </remarks>
    public static SanitizedException Sanitize(Exception exception, SecretMasker masker)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(masker);

        if (exception is SanitizedException existing)
        {
            return new SanitizedException(
                existing.OriginalExceptionType ?? typeof(SanitizedException).FullName!,
                SafeMasking.MaskText(masker, existing.Message),
                SafeMasking.MaskText(masker, existing.SanitizedDetails ?? existing.ToString()));
        }

        var type = exception.GetType();
        return new SanitizedException(
            type.FullName ?? type.Name,
            SafeMasking.MaskText(masker, exception.Message),
            SafeMasking.MaskText(masker, exception.ToString()));
    }

    private static Dictionary<string, string>? SanitizeData(IReadOnlyDictionary<string, string>? data, SecretMasker masker)
    {
        if (data is null)
        {
            return null;
        }

        var sanitized = new Dictionary<string, string>(data.Count, StringComparer.Ordinal);
        foreach (var entry in data)
        {
            // 兩道都走:名稱像祕密的欄位依名稱遮,其餘再做一次已登記祕密的字面替換。
            // 回應內容摘要(body)就是後者要攔的 —— 對方可能把金鑰 echo 回錯誤訊息裡。
            // Both passes apply: a field whose name looks secret is masked by name, and everything then gets
            // literal replacement of registered secrets. The response-body snippet is what the second pass is
            // for — a peer may echo the key back inside its error text.
            string value;
            try
            {
                value = masker.MaskNamedValue(entry.Key, entry.Value) ?? string.Empty;
            }
#pragma warning disable CA1031 // 遮罩失敗一律回替代字串,不回原文。A masking failure yields the placeholder, never the raw value.
            catch (Exception)
#pragma warning restore CA1031
            {
                value = SafeMasking.MaskingFailedPlaceholder;
            }

            sanitized[SafeMasking.MaskText(masker, entry.Key)] = SafeMasking.MaskText(masker, value);
        }

        return sanitized;
    }

    private static string MaskNonBlank(SecretMasker masker, string text)
    {
        // Error 不接受空白的代碼與訊息;遮罩字串本身不會是空白,這裡只是防止萬一的保險。
        // Error rejects a blank code or message; a mask segment is never blank, so this is only a safeguard.
        var masked = SafeMasking.MaskText(masker, text);
        return string.IsNullOrWhiteSpace(masked) ? SafeMasking.MaskingFailedPlaceholder : masked;
    }
}

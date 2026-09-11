using Ozakboy.Security.Masking;

namespace Ozakboy.Http.Logging;

/// <summary>
/// 遮罩的失敗封閉包裝:遮不了就整段捨棄,絕不退回原始值。
/// A fail-closed wrapper around masking: when it cannot mask, it discards the value rather than passing the
/// original through.
/// </summary>
/// <remarks>
/// 遮罩程式碼出問題時,最糟的處理方式是「遮不了就原樣輸出」—— 那等於把 API 金鑰與簽章直接寫進日誌,
/// 而且看起來一切正常,沒有任何跡象顯示剛剛漏了東西。這裡一律往安全的方向倒:寧可日誌少一行有用資訊。
/// The worst possible response to a bug in masking code is to emit the value unmasked: that writes the API key
/// and the signature straight into the log while everything still looks fine, with nothing to hint that
/// anything leaked. This falls the safe way instead, and accepts losing a line of useful detail.
/// </remarks>
internal static class SafeMasking
{
    /// <summary>
    /// 遮罩失敗時輸出的替代字串。
    /// The placeholder emitted when masking fails.
    /// </summary>
    public const string MaskingFailedPlaceholder = "<redacted:masking-failed>";

    /// <summary>
    /// 遮罩位址中敏感 query 參數的值,保留路徑與非敏感參數。
    /// Masks the values of sensitive query parameters, keeping the path and the harmless parameters.
    /// </summary>
    /// <param name="masker">遮罩器。The masker.</param>
    /// <param name="uri">位址,可為 <see langword="null"/>。The URI, which may be <see langword="null"/>.</param>
    /// <returns>可安全寫入日誌的位址字串。A URI string safe to log.</returns>
    public static string MaskUri(SecretMasker masker, Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        try
        {
            var text = uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString;
            return masker.MaskQueryString(text);
        }
#pragma warning disable CA1031 // 這裡刻意攔截所有例外:遮罩失敗絕不可讓原始位址流進日誌。
                              // Catching everything is the point: a masking failure must never let the raw URI reach the log.
        catch (Exception)
#pragma warning restore CA1031
        {
            return MaskingFailedPlaceholder;
        }
    }

    /// <summary>
    /// 對任意文字做已登記祕密的字面替換。
    /// Replaces every registered secret occurring in arbitrary text.
    /// </summary>
    /// <param name="masker">遮罩器。The masker.</param>
    /// <param name="text">文字,可為 <see langword="null"/>。The text, which may be <see langword="null"/>.</param>
    /// <returns>可安全寫出的文字;<see langword="null"/> 回傳空字串。Text safe to emit; <see langword="null"/> yields an empty string.</returns>
    /// <remarks>
    /// 例外訊息、錯誤訊息、錯誤資料都沒有「欄位名」可以判斷,能攔住祕密的只剩字面替換。
    /// 替換本身出錯時同樣往安全的方向倒:回替代字串,不回原文。
    /// Exception messages, error messages and error data carry no field name to judge by, so literal
    /// replacement is the only thing that can catch a secret there. If the replacement itself fails it falls
    /// the safe way too: the placeholder, never the original.
    /// </remarks>
    public static string MaskText(SecretMasker masker, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            return masker.MaskText(text);
        }
#pragma warning disable CA1031 // 同上:遮罩失敗一律回替代字串,不回原文。
                              // As above: a masking failure always yields the placeholder, never the raw text.
        catch (Exception)
#pragma warning restore CA1031
        {
            return MaskingFailedPlaceholder;
        }
    }

    /// <summary>
    /// 遮罩內容中的敏感欄位。內容是 JSON 時逐欄位遮罩,否則整段捨棄。
    /// Masks sensitive fields inside a body. JSON is masked field by field; anything else is discarded whole.
    /// </summary>
    /// <param name="masker">遮罩器。The masker.</param>
    /// <param name="body">內容。The body.</param>
    /// <param name="maxLength">保留的最大字元數。The maximum number of characters kept.</param>
    /// <returns>可安全寫入日誌的內容字串。A body string safe to log.</returns>
    /// <remarks>
    /// 非 JSON 的內容無法逐欄位判斷哪裡是祕密,只能整段捨棄 ——
    /// 表單編碼的下單請求裡就躺著 <c>signature</c>,靠關鍵字掃描攔不乾淨。
    /// A non-JSON body cannot be inspected field by field, so it is discarded entirely: a form-encoded order
    /// request has <c>signature</c> sitting right in it, and keyword scanning does not catch that reliably.
    /// </remarks>
    public static string MaskBody(SecretMasker masker, string body, int maxLength)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        try
        {
            if (!masker.TryMaskJson(body, out var masked) || masked is null)
            {
                return MaskingFailedPlaceholder;
            }

            return masked.Length > maxLength ? masked[..maxLength] : masked;
        }
#pragma warning disable CA1031 // 同上:遮罩失敗一律回替代字串,不回原始內容。
                              // As above: a masking failure always yields the placeholder, never the raw body.
        catch (Exception)
#pragma warning restore CA1031
        {
            return MaskingFailedPlaceholder;
        }
    }
}

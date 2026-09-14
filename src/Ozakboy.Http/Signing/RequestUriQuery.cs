using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http.Signing;

/// <summary>
/// 把已編碼的 query 字串寫進請求位址。簽章與未簽章兩條路徑共用這一份,送出的位址才會逐字一致。
/// Writes an encoded query string into a request URI. The signed and unsigned paths share this one
/// implementation so the URI that goes out is identical on both.
/// </summary>
/// <remarks>
/// 0.3.2 以前這段是 <see cref="SigningHandler"/> 的私有方法,「把參數寫進位址」因此只在掛了簽章處理器時才會發生;
/// <see cref="HttpPipelineOptions.EnableSigning"/> 為 <see langword="false"/> 的管線,<c>WithQueryParameters</c>
/// 放進去的參數一個都沒有送出去。拆成共用的一份,是讓 <see cref="QueryParametersHandler"/> 補上那條路徑時,
/// 合併規則不會與簽章路徑各寫一套、日後各自漂移。
/// Up to 0.3.2 this was a private method of <see cref="SigningHandler"/>, so writing parameters into the URI only
/// happened when the signing handler was attached; with <see cref="HttpPipelineOptions.EnableSigning"/> set to
/// <see langword="false"/>, nothing placed with <c>WithQueryParameters</c> ever went out. It is shared so that
/// <see cref="QueryParametersHandler"/>, which covers that path now, cannot grow a second set of merge rules that
/// drifts from the signing path's.
/// </remarks>
internal static class RequestUriQuery
{
    /// <summary>
    /// 換掉位址的 query 部分,保留 scheme、authority 與路徑。位址上原有的 query 一律捨棄,不做合併。
    /// Replaces the query part of a URI, keeping the scheme, authority, and path. Any query already on the URI is
    /// discarded rather than merged.
    /// </summary>
    /// <param name="uri">原位址,允許相對位址。The original URI; relative URIs are accepted.</param>
    /// <param name="query">
    /// 已編碼的 query 字串(不含 <c>?</c>);空字串時結果不帶 query。
    /// The encoded query string, without the <c>?</c>; an empty string yields a URI with no query.
    /// </param>
    /// <returns>換好 query 的位址。The URI with the new query.</returns>
    /// <exception cref="ResultException">
    /// <paramref name="uri"/> 為 <see langword="null"/> 時擲出,錯誤代碼為 <see cref="HttpErrorCodes.SigningMissingRequestUri"/>。
    /// Thrown when <paramref name="uri"/> is <see langword="null"/>, with the code
    /// <see cref="HttpErrorCodes.SigningMissingRequestUri"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// 刻意不用 <see cref="UriBuilder"/>:它會重新組裝位址,而我們需要的是送出的字串與簽過的字串
    /// 逐字相同。這裡以字串層級直接接上,而 <see cref="Uri.EscapeDataString(string)"/> 不會編碼未保留字元,
    /// 因此 <see cref="Uri"/> 也不會把我們的 <c>%XX</c> 還原回去。
    /// <see cref="UriBuilder"/> is avoided on purpose: it reassembles the URI, and what this needs is a string
    /// that matches the signed one exactly. The query is concatenated at the string level instead, and since
    /// <see cref="Uri.EscapeDataString(string)"/> never escapes unreserved characters, <see cref="Uri"/> has
    /// nothing of ours to unescape back.
    /// </para>
    /// <para>
    /// 原有的 query 捨棄而不合併,是簽章路徑的需要:它不在簽章輸入裡,留著就是「送出的比簽過的多」,對方一定驗不過。
    /// 未簽章路徑沿用同一條規則,兩條路徑對同一個請求才會送出同一個位址。
    /// The existing query is discarded rather than merged because the signing path needs it so: it is not part of
    /// the signature input, and keeping it would send more than was signed. The unsigned path follows the same
    /// rule so that both send the same URI for the same request.
    /// </para>
    /// </remarks>
    public static Uri Replace(Uri? uri, string query)
    {
        if (uri is null)
        {
            throw Error.Validation(
                HttpErrorCodes.SigningMissingRequestUri,
                "請求沒有目標位址,無法附加參數。The request has no target URI, so parameters cannot be attached.").ToException();
        }

        var text = uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Path) : uri.OriginalString;
        var separator = text.IndexOf('?', StringComparison.Ordinal);
        if (separator >= 0)
        {
            text = text[..separator];
        }

        var target = query.Length == 0 ? text : $"{text}?{query}";
        return new Uri(target, uri.IsAbsoluteUri ? UriKind.Absolute : UriKind.Relative);
    }
}

namespace Ozakboy.Http.Signing;

/// <summary>
/// 未簽章管線上,把請求攜帶的參數寫進位址。
/// On an unsigned pipeline, writes the request's parameters into the URI.
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼需要這一段。</b><c>WithQueryParameters</c> 只把參數放進 <see cref="HttpRequestMessage.Options"/>,
/// 真正寫進 <see cref="HttpRequestMessage.RequestUri"/> 的一直是 <see cref="SigningHandler"/>。
/// <see cref="HttpPipelineOptions.EnableSigning"/> 為 <see langword="false"/> 時簽章處理器根本不掛,
/// 於是 0.3.2 以前,未簽章管線上的每一個 query 參數都安靜地消失:請求照樣送出、照樣拿到回應,
/// 只是對方回的是「缺少必要參數」(幣安是 <c>-1102</c>),完全看不出是本地少送了東西。
/// <b>Why this exists.</b> <c>WithQueryParameters</c> only places the parameters in
/// <see cref="HttpRequestMessage.Options"/>; writing them into <see cref="HttpRequestMessage.RequestUri"/> was always
/// <see cref="SigningHandler"/>'s job. With <see cref="HttpPipelineOptions.EnableSigning"/> set to
/// <see langword="false"/> the signing handler is not attached at all, so up to 0.3.2 every query parameter on an
/// unsigned pipeline vanished without a trace: the request still went out and a response still came back, only
/// the peer answered "mandatory parameter missing" (<c>-1102</c> on Binance), with nothing pointing at a parameter
/// dropped locally.
/// </para>
/// <para>
/// <b>位置與寫入規則都與簽章處理器相同。</b>它掛在簽章處理器原本的位置(限流之內、日誌之外),
/// 日誌因此記到的是實際送出的位址;參數以同一個 <see cref="QueryParameters.ToQueryString"/> 編碼,
/// 以同一個 <see cref="RequestUriQuery.Replace"/> 寫入 —— 位址上原有的 query 被換掉而不是合併,
/// 沒有參數宣告的請求原封不動。同一個請求在簽與不簽的管線上送出的 query(不計簽章本身)逐字相同。
/// <b>Its position and write rule match the signing handler's.</b> It sits where the signing handler would —
/// inside rate limiting, outside logging — so the log records the URI actually sent; the parameters are encoded
/// by the same <see cref="QueryParameters.ToQueryString"/> and written by the same
/// <see cref="RequestUriQuery.Replace"/>: a query already on the URI is replaced rather than merged, and a request
/// that declares no parameters passes through untouched. The query a request sends is therefore identical, byte
/// for byte and signature aside, on a signed and an unsigned pipeline.
/// </para>
/// <para>
/// <b>簽章管線上不掛它</b>,由簽章處理器一次寫入;兩段都掛也不會重複,因為寫入是「換掉」而非「附加」,
/// 但那樣的註冊沒有意義,本套件不會那樣組。這個處理器不簽章:在未簽章管線上標了
/// <c>WithSignature</c> 的請求,參數照樣寫入,但不會帶簽章與金鑰標頭,與 0.3.2 以前「不掛簽章處理器」的語意一致。
/// <b>It is not attached to a signing pipeline</b>, where the signing handler writes the query once; attaching both
/// would not duplicate anything, since writing replaces rather than appends, but such a registration serves no
/// purpose and this package never builds one. The handler does not sign: a request marked <c>WithSignature</c> on
/// an unsigned pipeline gets its parameters written but no signature and no key header, consistent with what "no
/// signing handler" has always meant.
/// </para>
/// </remarks>
internal sealed class QueryParametersHandler : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request.GetQueryParameters();
        if (parameters is not null)
        {
            // 每次嘗試都會走到這裡(重試在外層、每次嘗試複製一份原請求),寫入的是同一份不可變參數,結果每次都相同。
            // Every attempt passes through here (retry sits outside and clones the original request per attempt),
            // and the same immutable parameters are written each time, so every attempt sends the same query.
            request.RequestUri = RequestUriQuery.Replace(request.RequestUri, parameters.ToQueryString());
        }

        return base.SendAsync(request, cancellationToken);
    }
}

using System.Net.Http.Headers;

namespace Ozakboy.Http.Retry;

/// <summary>
/// 複製 <see cref="HttpRequestMessage"/>,讓同一個請求能送出第二次。
/// Clones an <see cref="HttpRequestMessage"/> so the same request can go out a second time.
/// </summary>
/// <remarks>
/// <see cref="HttpRequestMessage"/> 是一次性的:送出後內容串流已被讀完,直接重送會拋
/// <see cref="InvalidOperationException"/>。重試因此必須複製,而內容必須先在記憶體裡緩衝起來。
/// An <see cref="HttpRequestMessage"/> is single-use: once sent, its content stream has been consumed and
/// resending it throws <see cref="InvalidOperationException"/>. Retrying therefore has to clone, and the
/// content has to be buffered in memory first.
/// </remarks>
internal static class HttpRequestCloner
{
    /// <summary>
    /// 先把請求內容讀進位元組陣列,供後續每次嘗試重建內容使用。
    /// Reads the request content into a byte array once, so every attempt can rebuild the content from it.
    /// </summary>
    /// <param name="request">原請求。The original request.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>內容位元組;沒有內容時為 <see langword="null"/>。The content bytes, or <see langword="null"/> when there is none.</returns>
    public static async Task<byte[]?> BufferContentAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null)
        {
            return null;
        }

        return await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 複製請求。標頭、選項與版本設定一併帶過去。
    /// Clones the request, carrying over headers, options, and version settings.
    /// </summary>
    /// <param name="request">原請求。The original request.</param>
    /// <param name="contentBytes">先前緩衝好的內容位元組。The previously buffered content bytes.</param>
    /// <returns>可再次送出的請求。A request that can be sent again.</returns>
    public static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? contentBytes)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // 選項承載了本套件的每請求宣告(權重、冪等、重試策略),不複製過去的話,
        // 重試出去的那份請求會退回預設行為 —— 而且不會有任何錯誤訊息。
        // The options carry this package's per-request declarations (weight, idempotency, retry policy). Left
        // behind, the retried request silently falls back to default behaviour with nothing to show for it.
        foreach (var option in (IDictionary<string, object?>)request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        if (contentBytes is not null)
        {
            var content = new ByteArrayContent(contentBytes);
            CopyContentHeaders(request.Content?.Headers, content.Headers);
            clone.Content = content;
        }

        return clone;
    }

    private static void CopyContentHeaders(HttpContentHeaders? source, HttpContentHeaders target)
    {
        if (source is null)
        {
            return;
        }

        foreach (var header in source)
        {
            target.TryAddWithoutValidation(header.Key, header.Value);
        }
    }
}

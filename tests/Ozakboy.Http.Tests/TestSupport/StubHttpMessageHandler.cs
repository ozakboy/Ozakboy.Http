using System.Net;

namespace Ozakboy.Http.Tests.TestSupport;

/// <summary>
/// 假的最內層處理器:記錄收到的請求,並依腳本回傳回應。測試全程不連網。
/// A stub innermost handler that records the requests it receives and replies from a script. Tests never touch
/// the network.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _responder;
    private int _callCount;

    public StubHttpMessageHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    /// <summary>
    /// 收到的請求快照(位址、方法、標頭),依收到順序排列。
    /// Snapshots of the requests received, in arrival order.
    /// </summary>
    public List<RequestSnapshot> Requests { get; } = [];

    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>
    /// 每次都回同一個狀態碼。
    /// Always replies with the same status code.
    /// </summary>
    public static StubHttpMessageHandler AlwaysReturns(HttpStatusCode statusCode, string content = "") =>
        new((_, _, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content),
        }));

    /// <summary>
    /// 依序回傳指定的狀態碼;腳本用完後重複最後一個。
    /// Replies with the given status codes in order, repeating the last one once the script runs out.
    /// </summary>
    public static StubHttpMessageHandler ReturnsSequence(params HttpStatusCode[] statusCodes) =>
        new((_, attempt, _) =>
        {
            var index = Math.Min(attempt - 1, statusCodes.Length - 1);
            return Task.FromResult(new HttpResponseMessage(statusCodes[index]));
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _callCount);

        lock (Requests)
        {
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri,
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
                request.Content is null ? null : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()));
        }

        return await _responder(request, attempt, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record RequestSnapshot(
        HttpMethod Method,
        Uri? RequestUri,
        Dictionary<string, string> Headers,
        string? Body);
}

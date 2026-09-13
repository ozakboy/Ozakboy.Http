namespace Ozakboy.Http.Tests.TestSupport;

/// <summary>
/// 會計數的假工廠:記錄 <see cref="CreateClient"/> 被呼叫幾次、每次拿的是哪個名稱。
/// A counting stub factory that records how many times <see cref="CreateClient"/> was called and under which
/// names.
/// </summary>
/// <remarks>
/// 真正的 <see cref="IHttpClientFactory"/> 只在每次 <see cref="CreateClient"/> 時才有機會輪替處理器,
/// 所以「有沒有每次請求都取一個新的用戶端」是可以直接數出來的 —— 這個假工廠就是拿來數的。
/// 回傳的用戶端刻意不持有處理器的生命週期(<c>disposeHandler: false</c>),與真正的工廠一致:
/// 處理器是共用且被池化的,不該隨任何一個用戶端被釋放。
/// A real <see cref="IHttpClientFactory"/> only gets the chance to rotate handlers on each
/// <see cref="CreateClient"/> call, so whether a client is taken per request is something that can simply be
/// counted — which is what this stub is for. The clients it returns deliberately do not own the handler's
/// lifetime (<c>disposeHandler: false</c>), matching the real factory: the handler is shared and pooled, and
/// must not go away with any one client.
/// </remarks>
internal sealed class CountingHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    private int _createClientCount;

    public CountingHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    /// <summary>
    /// <see cref="CreateClient"/> 被呼叫的次數。The number of <see cref="CreateClient"/> calls.
    /// </summary>
    public int CreateClientCount => Volatile.Read(ref _createClientCount);

    /// <summary>
    /// 每次被要求的用戶端名稱,依呼叫順序排列。The client names requested, in call order.
    /// </summary>
    public List<string> RequestedNames { get; } = [];

    public HttpClient CreateClient(string name)
    {
        Interlocked.Increment(ref _createClientCount);

        lock (RequestedNames)
        {
            RequestedNames.Add(name);
        }

        // 逾時交給管線,與 AddOzakboyHttpPipeline 對真正的具名用戶端所做的一致。
        // Timeouts belong to the pipeline, matching what AddOzakboyHttpPipeline does to a real named client.
        return new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }
}

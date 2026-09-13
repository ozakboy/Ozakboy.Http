namespace Ozakboy.Http.Tests.TestSupport;

/// <summary>
/// 在自己的 <see cref="DisposeAsync"/> 裡送出一次收尾請求的服務,用來驗證容器釋放時管線還活著。
/// A service that sends one farewell request from its own <see cref="DisposeAsync"/>, used to check that the
/// pipeline is still alive while the container is tearing down.
/// </summary>
/// <remarks>
/// 這是真實存在的用法而不是為了測試杜撰的:幣安的使用者資料串流在收尾時必須 <c>DELETE</c> 掉 listenKey,
/// 沒送出去的話那把串流憑證會留到自然過期。這種服務對釋放順序的要求很具體 ——
/// 它所相依的管線必須比它更早被建立,容器才會比它更晚釋放那些東西。
/// This is a real usage rather than one invented for the test: the Binance user data stream has to <c>DELETE</c>
/// its listenKey on the way out, and a request that never goes out leaves the stream credential to lapse on its
/// own. Such a service makes a specific demand of disposal order — the pipeline it depends on must have been
/// created before it, so that the container disposes of that pipeline after it.
/// </remarks>
internal sealed class FarewellRequestService : IAsyncDisposable
{
    public FarewellRequestService(HttpPipelineClient pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        Pipeline = pipeline;
    }

    /// <summary>
    /// 這個服務所用的管線門面。The pipeline facade this service sends through.
    /// </summary>
    public HttpPipelineClient Pipeline { get; }

    /// <summary>
    /// 收尾請求是否送出成功。Whether the farewell request went out successfully.
    /// </summary>
    public bool FarewellSucceeded { get; private set; }

    /// <summary>
    /// 收尾請求失敗時的說明;成功時為 <see langword="null"/>。
    /// Why the farewell request failed; <see langword="null"/> when it succeeded.
    /// </summary>
    public string? FarewellError { get; private set; }

    public async ValueTask DisposeAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "https://example.test/credential");

        // 例外一併接住:這一條要驗的是「收尾請求送不送得出去」,讓例外從釋放路徑逸出只會變成另一個症狀。
        // Exceptions are caught too: what is under test is whether the farewell request goes out, and letting one
        // escape the disposal path would only surface as a different symptom.
        try
        {
            var result = await Pipeline.SendAsync(request, CancellationToken.None).ConfigureAwait(false);

            FarewellSucceeded = result.IsSuccess;
            FarewellError = result.Error?.Message;

            result.GetValueOrDefault()?.Dispose();
        }
        catch (Exception exception)
        {
            FarewellSucceeded = false;
            FarewellError = $"{exception.GetType().Name}: {exception.Message}";
        }
    }
}

namespace Ozakboy.Http;

/// <summary>
/// <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/> 以用戶端名稱為鍵登記的管線資訊,
/// 供 <see cref="OzakboyHttpServiceProviderExtensions.CreateOzakboyHttpPipelineClient"/> 建立門面時使用。
/// Pipeline details registered by <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/>,
/// keyed by client name, for <see cref="OzakboyHttpServiceProviderExtensions.CreateOzakboyHttpPipelineClient"/>
/// to build the facade from.
/// </summary>
/// <remarks>
/// 門面的整體逾時必須與管線裡重試處理器用的是同一份設定;由呼叫端另外傳一份,兩邊就可能各說各話
/// (例如整體逾時短於單次嘗試逾時,第一次嘗試就註定跑不完)。
/// The facade's overall timeout has to come from the same settings the pipeline's retry handler uses; a
/// second copy passed in by the caller can drift from the first (an overall timeout shorter than the attempt
/// timeout, say, which dooms the very first attempt).
/// </remarks>
internal sealed class ClientPipelineRegistration
{
    public ClientPipelineRegistration(HttpTimeoutOptions timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        Timeouts = timeouts;
    }

    public HttpTimeoutOptions Timeouts { get; }
}

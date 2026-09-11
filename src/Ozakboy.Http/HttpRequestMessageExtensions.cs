using Ozakboy.Core.Abstractions;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;

namespace Ozakboy.Http;

/// <summary>
/// 在單一請求上宣告管線行為:是否簽章、帶哪些參數、消耗多少限流權重、可不可以重試。
/// Declares per-request pipeline behaviour: whether to sign, which parameters to carry, how much rate-limit
/// weight to consume, and whether the request may be retried.
/// </summary>
/// <remarks>
/// 這些宣告存放在 <see cref="HttpRequestMessage.Options"/>,因此跟著請求走過整條管線,
/// 各處理器不需要彼此認識,也不需要另外傳遞脈絡物件。
/// The declarations live in <see cref="HttpRequestMessage.Options"/>, so they travel with the request through
/// the whole pipeline; handlers need not know about one another or pass a separate context object around.
/// </remarks>
public static class HttpRequestMessageExtensions
{
    private static readonly HttpRequestOptionsKey<bool> RequiresSignatureKey = new("Ozakboy.Http.RequiresSignature");
    private static readonly HttpRequestOptionsKey<QueryParameters> QueryParametersKey = new("Ozakboy.Http.QueryParameters");
    private static readonly HttpRequestOptionsKey<int> RequestWeightKey = new("Ozakboy.Http.RequestWeight");
    private static readonly HttpRequestOptionsKey<RequestIdempotency> IdempotencyKey = new("Ozakboy.Http.Idempotency");
    private static readonly HttpRequestOptionsKey<RetryPolicy> RetryPolicyKey = new("Ozakboy.Http.RetryPolicy");

    /// <summary>
    /// 指定這個請求要攜帶的參數。順序即簽章順序,也是實際送出的順序。
    /// Sets the parameters this request carries. Their order is the signing order and the wire order.
    /// </summary>
    /// <param name="request">請求。The request.</param>
    /// <param name="parameters">參數序列。The parameter sequence.</param>
    /// <returns>請求本身,便於串接。The request, for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// 任一參數為 <see langword="null"/> 時擲出。Thrown when either argument is <see langword="null"/>.
    /// </exception>
    public static HttpRequestMessage WithQueryParameters(this HttpRequestMessage request, QueryParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(parameters);

        request.Options.Set(QueryParametersKey, parameters);
        return request;
    }

    /// <summary>
    /// 取得這個請求攜帶的參數。
    /// Gets the parameters this request carries.
    /// </summary>
    /// <param name="request">請求。The request.</param>
    /// <returns>參數序列;未設定時為 <see langword="null"/>。The parameters, or <see langword="null"/> when unset.</returns>
    public static QueryParameters? GetQueryParameters(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Options.TryGetValue(QueryParametersKey, out var parameters) ? parameters : null;
    }

    /// <summary>
    /// 標記這個請求需要簽章。
    /// Marks this request as requiring a signature.
    /// </summary>
    /// <param name="request">請求。The request.</param>
    /// <returns>請求本身。The request.</returns>
    public static HttpRequestMessage WithSignature(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Options.Set(RequiresSignatureKey, true);
        return request;
    }

    /// <summary>
    /// 這個請求是否需要簽章。
    /// Whether this request requires a signature.
    /// </summary>
    /// <param name="request">請求。The request.</param>
    /// <returns>需要簽章時為 <see langword="true"/>。<see langword="true"/> when a signature is required.</returns>
    public static bool RequiresSignature(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Options.TryGetValue(RequiresSignatureKey, out var value) && value;
    }

    /// <summary>
    /// 宣告這個請求消耗的限流權重。
    /// Declares how much rate-limit weight this request consumes.
    /// </summary>
    /// <param name="request">請求。The request.</param>
    /// <param name="weight">權重,必須為正整數。The weight; must be positive.</param>
    /// <returns>請求本身。The request.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="weight"/> 小於 1 時擲出。Thrown when <paramref name="weight"/> is less than 1.
    /// </exception>
    public static HttpRequestMessage WithWeight(this HttpRequestMessage request, int weight)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(weight, 1);

        request.Options.Set(RequestWeightKey, weight);
        return request;
    }

    /// <summary>
    /// 取得這個請求宣告的限流權重。
    /// Gets the rate-limit weight declared for this request.
    /// </summary>
    /// <param name="request">請求。The request.</param>
    /// <returns>權重;未宣告時為 <see langword="null"/>。The weight, or <see langword="null"/> when undeclared.</returns>
    public static int? GetWeight(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Options.TryGetValue(RequestWeightKey, out var weight) ? weight : null;
    }

    /// <summary>
    /// 明確宣告這個請求是冪等的,允許重試。
    /// Explicitly declares this request idempotent, allowing retries.
    /// </summary>
    /// <param name="request">請求。The request.</param>
    /// <returns>請求本身。The request.</returns>
    /// <remarks>
    /// <b>只有在重送絕對不會產生第二次副作用時才呼叫這個方法。</b>逾時不代表對方沒收到 ——
    /// 連線在回應送回來的路上斷掉,對方那邊的動作早就完成了。在交易系統裡,對下單請求誤標冪等的代價是重複下單。
    /// 真正安全的做法是請求本身帶有由呼叫端產生的唯一識別(例如自訂訂單編號),讓對方能辨識並拒絕重複。
    /// <b>Call this only when resending genuinely cannot cause a second side effect.</b> A timeout does not
    /// mean the peer never received the request: the connection can drop while the response is on its way back,
    /// long after the peer has acted. In a trading system, mislabelling an order request as idempotent means
    /// placing the order twice. What actually makes it safe is a caller-generated unique identifier on the
    /// request (a client order id, say) that lets the peer recognise and reject the duplicate.
    /// </remarks>
    public static HttpRequestMessage AsIdempotent(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Options.Set(IdempotencyKey, RequestIdempotency.Idempotent);
        return request;
    }

    /// <summary>
    /// 明確宣告這個請求不是冪等的,禁止重試。
    /// Explicitly declares this request non-idempotent, forbidding retries.
    /// </summary>
    /// <param name="request">請求。The request.</param>
    /// <returns>請求本身。The request.</returns>
    /// <remarks>
    /// 用於覆蓋方法推定:即使是 <c>GET</c>,若對方把它實作成有副作用的端點,也應該明確關掉重試。
    /// Use this to override the method-based inference: even a <c>GET</c> should have retries switched off
    /// when the peer has implemented it as an endpoint with side effects.
    /// </remarks>
    public static HttpRequestMessage AsNonIdempotent(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Options.Set(IdempotencyKey, RequestIdempotency.NonIdempotent);
        return request;
    }

    /// <summary>
    /// 取得這個請求的冪等宣告。
    /// Gets this request's idempotency declaration.
    /// </summary>
    /// <param name="request">請求。The request.</param>
    /// <returns>
    /// 宣告值;未宣告時為 <see cref="RequestIdempotency.Inferred"/>。
    /// The declaration, or <see cref="RequestIdempotency.Inferred"/> when none was made.
    /// </returns>
    public static RequestIdempotency GetIdempotency(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Options.TryGetValue(IdempotencyKey, out var value) ? value : RequestIdempotency.Inferred;
    }

    /// <summary>
    /// 為這個請求指定專屬的重試策略,覆蓋處理器的預設值。
    /// Overrides the handler's default retry policy for this request.
    /// </summary>
    /// <param name="request">請求。The request.</param>
    /// <param name="policy">重試策略。The retry policy.</param>
    /// <returns>請求本身。The request.</returns>
    public static HttpRequestMessage WithRetryPolicy(this HttpRequestMessage request, RetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);

        request.Options.Set(RetryPolicyKey, policy);
        return request;
    }

    /// <summary>
    /// 取得這個請求專屬的重試策略。
    /// Gets the retry policy specific to this request.
    /// </summary>
    /// <param name="request">請求。The request.</param>
    /// <returns>策略;未指定時為 <see langword="null"/>。The policy, or <see langword="null"/> when unset.</returns>
    public static RetryPolicy? GetRetryPolicy(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Options.TryGetValue(RetryPolicyKey, out var policy) ? policy : null;
    }
}

using Microsoft.Extensions.Options;
using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http.Retry;

/// <summary>
/// 依 <see cref="RetryPolicy"/> 重試暫時性失敗,並施加單次嘗試逾時。
/// Retries transient failures according to a <see cref="RetryPolicy"/>, and applies the per-attempt timeout.
/// </summary>
/// <remarks>
/// <para>
/// <b>非冪等的請求絕對不重試。</b>逾時只代表「沒收到回應」,不代表「對方沒收到請求」——
/// 連線可能是在回應回來的路上斷的,那筆訂單早就成立了。重送的代價在交易系統就是重複下單,
/// 而它造成的部位偏差往往要到對帳時才被發現。
/// 因此預設只有安全方法會重試,<c>POST</c>、<c>DELETE</c>、<c>PUT</c>、<c>PATCH</c> 一律不重試,
/// 除非呼叫端用 <see cref="HttpRequestMessageExtensions.AsIdempotent"/> 明確擔保。
/// <b>Non-idempotent requests are never retried.</b> A timeout only says no response arrived, not that the
/// peer never received the request: the connection may well have dropped on the way back, long after the order
/// was accepted. Resending means placing it twice, and the resulting position drift often surfaces only at
/// reconciliation. Only safe methods are retried by default; <c>POST</c>, <c>DELETE</c>, <c>PUT</c>, and
/// <c>PATCH</c> are not, unless the caller vouches for the request with
/// <see cref="HttpRequestMessageExtensions.AsIdempotent"/>.
/// </para>
/// <para>
/// 退避間隔一律由 <see cref="RetryPolicy.GetDelay(int)"/> 計算;回應若帶 <c>Retry-After</c>,則以對方指定的
/// 時間為準(上限見 <see cref="RetryOptions.MaxRetryAfter"/>)。
/// Backoff comes from <see cref="RetryPolicy.GetDelay(int)"/>; when the response carries <c>Retry-After</c>,
/// the peer's instruction wins instead, capped by <see cref="RetryOptions.MaxRetryAfter"/>.
/// </para>
/// </remarks>
public sealed class RetryHandler : DelegatingHandler
{
    private readonly RetryOptions _options;
    private readonly HttpTimeoutOptions _timeouts;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 建立重試處理器。
    /// Creates the retry handler.
    /// </summary>
    /// <param name="options">重試設定。The retry options.</param>
    /// <param name="timeouts">逾時設定;<see langword="null"/> 時使用預設值。The timeout options; defaults are used when <see langword="null"/>.</param>
    /// <param name="timeProvider">時間來源;測試請傳入假時鐘。The time source; tests pass a fake clock.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定不合法時擲出。Thrown when the options are invalid.
    /// </exception>
    public RetryHandler(RetryOptions options, HttpTimeoutOptions? timeouts = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        var effectiveTimeouts = timeouts ?? new HttpTimeoutOptions();
        var timeoutValidation = effectiveTimeouts.Validate();
        if (timeoutValidation.IsFailure)
        {
            throw new ArgumentException(timeoutValidation.Error.Message, nameof(timeouts));
        }

        _options = options;
        _timeouts = effectiveTimeouts;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 以選項容器建立處理器,供相依性注入使用。
    /// Creates the handler from options containers, for dependency injection.
    /// </summary>
    /// <param name="options">重試設定容器。The retry options container.</param>
    /// <param name="timeouts">逾時設定容器。The timeout options container.</param>
    /// <param name="timeProvider">時間來源。The time source.</param>
    public RetryHandler(IOptions<RetryOptions> options, IOptions<HttpTimeoutOptions> timeouts, TimeProvider? timeProvider = null)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).Value,
            (timeouts ?? throw new ArgumentNullException(nameof(timeouts))).Value,
            timeProvider)
    {
    }

    /// <summary>
    /// 判斷請求是否允許重試。
    /// Decides whether a request may be retried.
    /// </summary>
    /// <param name="request">請求。The request.</param>
    /// <returns>允許重試時為 <see langword="true"/>。<see langword="true"/> when retries are allowed.</returns>
    public static bool IsRetryable(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.GetIdempotency() switch
        {
            RequestIdempotency.Idempotent => true,
            RequestIdempotency.NonIdempotent => false,
            _ => IsSafeMethod(request.Method),
        };
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var policy = request.GetRetryPolicy() ?? _options.Policy;

        if (policy.MaxAttempts <= 1 || !IsRetryable(request))
        {
            return await SendAttemptAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // 內容只緩衝一次。不可重試的路徑(所有下單請求都走那裡)完全不會走到這行,不必付出複製成本。
        // The content is buffered once. The non-retryable path — where every order request goes — never
        // reaches this line and pays no copying cost.
        var contentBytes = await HttpRequestCloner.BufferContentAsync(request, cancellationToken).ConfigureAwait(false);

        for (var attempt = 1; ; attempt++)
        {
            TimeSpan? retryAfter = null;

            // 每次嘗試都用一份新的請求副本:HttpRequestMessage 是一次性的,重送原件會直接拋例外。
            // Each attempt gets a fresh copy: an HttpRequestMessage is single-use and resending the original
            // throws outright.
            using (var attemptRequest = HttpRequestCloner.Clone(request, contentBytes))
            {
                Error error;

                try
                {
                    var response = await SendAttemptAsync(attemptRequest, cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return response;
                    }

                    error = _options.ErrorBodySnippetLength > 0
                        ? await HttpErrorMapper.FromResponseAsync(response, _options.ErrorBodySnippetLength, cancellationToken).ConfigureAwait(false)
                        : HttpErrorMapper.FromStatusCode(response.StatusCode, response.ReasonPhrase);

                    if (!policy.ShouldRetry(attempt, error))
                    {
                        return response;
                    }

                    if (_options.RespectRetryAfter && HttpErrorMapper.TryGetRetryAfter(response, _timeProvider, out var after))
                    {
                        retryAfter = after > _options.MaxRetryAfter ? _options.MaxRetryAfter : after;
                    }

                    response.Dispose();
                }
                catch (HttpPipelineException exception)
                {
                    if (!policy.ShouldRetry(attempt, exception.Error))
                    {
                        throw;
                    }
                }
                catch (HttpRequestException exception)
                {
                    if (!policy.ShouldRetry(attempt, HttpErrorMapper.FromException(exception)))
                    {
                        throw;
                    }
                }
            }

            var delay = retryAfter ?? policy.GetDelay(attempt);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSafeMethod(HttpMethod method) =>
        method == HttpMethod.Get
        || method == HttpMethod.Head
        || method == HttpMethod.Options
        || method == HttpMethod.Trace;

    private async Task<HttpResponseMessage> SendAttemptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(_timeouts.AttemptTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            return await base.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new HttpPipelineException(
                new Error(
                    HttpErrorCodes.AttemptTimeout,
                    $"單次嘗試超過 {_timeouts.AttemptTimeout} 未完成。A single attempt did not complete within {_timeouts.AttemptTimeout}.",
                    ErrorCategory.Timeout)
                {
                    Exception = exception,
                });
        }
    }
}

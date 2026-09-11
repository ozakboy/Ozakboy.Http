using Ozakboy.Core.Abstractions;
using Ozakboy.Security.Masking;

namespace Ozakboy.Http;

/// <summary>
/// 管線的對外門面:把例外路徑收斂回 <see cref="Result{T}"/>,並施加整體逾時。
/// The pipeline's outward facade: it folds the exception path back into <see cref="Result{T}"/> and applies
/// the overall timeout.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HttpClient"/> 以例外表達失敗,本套件以 <see cref="Result{T}"/> 表達 —— 轉換就發生在這裡。
/// 呼叫端因此不必記得哪些例外要攔、哪些是預期的失敗,所有結果都是同一種形狀。
/// <see cref="HttpClient"/> reports failures as exceptions and this package reports them as
/// <see cref="Result{T}"/>; the conversion happens here. Callers therefore need not remember which exceptions
/// to catch and which failures are expected — every outcome has the same shape.
/// </para>
/// <para>
/// <b>非 2xx 回應不算失敗。</b><see cref="SendAsync"/> 只在「請求沒送出去或沒拿到回應」時回傳失敗;
/// 拿到 4xx 仍算成功取得回應,狀態碼怎麼解讀由呼叫端決定(不同服務對同一個狀態碼的用法差很多)。
/// 想直接把非 2xx 當失敗處理,用 <see cref="SendForStringAsync"/>。
/// <b>A non-2xx response is not a failure.</b> <see cref="SendAsync"/> reports failure only when the request
/// never went out or no response came back; a 4xx still counts as having obtained a response, and what the
/// status means is the caller's call, since services differ widely in how they use the same code. To treat
/// non-2xx as failure directly, use <see cref="SendForStringAsync"/>.
/// </para>
/// <para>
/// <b>這裡是錯誤離開本套件前的最後一道關口。</b>回傳的每一個失敗都經過遮罩:代碼、訊息、每一筆資料都做已登記祕密的
/// 字面替換,<see cref="Error.Exception"/> 一律換成 <see cref="SanitizedException"/>。遮罩用的是建構時傳入的遮罩器;
/// 請傳入具名用戶端的那一個(<see cref="OzakboyHttpServiceProviderExtensions.GetOzakboyHttpMasker"/>),
/// 否則這道關口只認得 <see cref="SecretMasker.Default"/> 上的祕密。
/// <b>This is the last checkpoint errors pass before leaving the package.</b> Every failure returned is masked:
/// the code, the message and every data entry get literal replacement of registered secrets, and
/// <see cref="Error.Exception"/> is always a <see cref="SanitizedException"/>. The masker is the one supplied
/// at construction; pass the named client's (<see cref="OzakboyHttpServiceProviderExtensions.GetOzakboyHttpMasker"/>),
/// or this checkpoint knows only the secrets on <see cref="SecretMasker.Default"/>.
/// </para>
/// </remarks>
public sealed class HttpPipelineClient
{
    private readonly HttpClient _httpClient;
    private readonly HttpTimeoutOptions _timeouts;
    private readonly TimeProvider _timeProvider;
    private readonly SecretMasker _masker;

    /// <summary>
    /// 建立門面。
    /// Creates the facade.
    /// </summary>
    /// <param name="httpClient">
    /// 已組好管線的用戶端。通常來自 <see cref="IHttpClientFactory"/>。
    /// The client with the pipeline already assembled, usually from <see cref="IHttpClientFactory"/>.
    /// </param>
    /// <param name="timeouts">逾時設定;<see langword="null"/> 時使用預設值。The timeout options; defaults are used when <see langword="null"/>.</param>
    /// <param name="timeProvider">時間來源;測試請傳入假時鐘。The time source; tests pass a fake clock.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpClient"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="httpClient"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 逾時設定不合法時擲出。Thrown when the timeout options are invalid.
    /// </exception>
    public HttpPipelineClient(HttpClient httpClient, HttpTimeoutOptions? timeouts = null, TimeProvider? timeProvider = null)
        : this(httpClient, timeouts, timeProvider, null)
    {
    }

    /// <summary>
    /// 建立門面,並指定錯誤邊界使用的遮罩器。
    /// Creates the facade with the masker its error boundary uses.
    /// </summary>
    /// <param name="httpClient">
    /// 已組好管線的用戶端。通常來自 <see cref="IHttpClientFactory"/>。
    /// The client with the pipeline already assembled, usually from <see cref="IHttpClientFactory"/>.
    /// </param>
    /// <param name="timeouts">逾時設定;<see langword="null"/> 時使用預設值。The timeout options; defaults are used when <see langword="null"/>.</param>
    /// <param name="timeProvider">時間來源;測試請傳入假時鐘。The time source; tests pass a fake clock.</param>
    /// <param name="masker">
    /// 遮罩器,通常是 <see cref="OzakboyHttpServiceProviderExtensions.GetOzakboyHttpMasker"/> 取得的那一個;
    /// <see langword="null"/> 時使用 <see cref="SecretMasker.Default"/>。
    /// The masker, usually the one from <see cref="OzakboyHttpServiceProviderExtensions.GetOzakboyHttpMasker"/>;
    /// <see cref="SecretMasker.Default"/> when <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpClient"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="httpClient"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 逾時設定不合法時擲出。Thrown when the timeout options are invalid.
    /// </exception>
    public HttpPipelineClient(HttpClient httpClient, HttpTimeoutOptions? timeouts, TimeProvider? timeProvider, SecretMasker? masker)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        var effectiveTimeouts = timeouts ?? new HttpTimeoutOptions();
        var validation = effectiveTimeouts.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(timeouts));
        }

        _httpClient = httpClient;
        _timeouts = effectiveTimeouts;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _masker = masker ?? SecretMasker.Default;
    }

    /// <summary>
    /// 送出請求並取得回應。
    /// Sends the request and returns the response.
    /// </summary>
    /// <param name="request">請求。呼叫端負責釋放。The request; the caller disposes it.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 成功時為回應(呼叫端負責釋放);請求未送出或未取得回應時為失敗。
    /// The response on success, which the caller disposes; a failure when the request never went out or no
    /// response was obtained.
    /// </returns>
    public async Task<Result<HttpResponseMessage>> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var timeoutSource = new CancellationTokenSource(_timeouts.OverallTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            return await _httpClient.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (ResultException exception)
        {
            return Result.Failure<HttpResponseMessage>(ErrorSanitizer.Sanitize(exception.Error, _masker));
        }
        catch (HttpRequestException exception)
        {
            return Result.Failure<HttpResponseMessage>(HttpErrorMapper.FromException(exception, _masker));
        }
        catch (OperationCanceledException exception)
        {
            return Result.Failure<HttpResponseMessage>(ErrorSanitizer.Sanitize(
                DescribeCancellation(exception, timeoutSource, _timeouts.OverallTimeout, cancellationToken),
                _masker));
        }
    }

    /// <summary>
    /// 送出請求並讀回字串內容。非 2xx 回應視為失敗。
    /// Sends the request and reads the body as a string. A non-2xx response counts as a failure.
    /// </summary>
    /// <param name="request">請求。呼叫端負責釋放。The request; the caller disposes it.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 成功時為回應內容;傳輸失敗或狀態碼非 2xx 時為失敗,錯誤分類已能反映是否值得重試。
    /// The response body on success; a failure on transport problems or a non-2xx status, with a category that
    /// already says whether retrying is worthwhile.
    /// </returns>
    /// <remarks>
    /// 「送出」與「讀內容」是兩個各自可能失敗的步驟,以 <see cref="ResultExtensions.ThenAsync{T, TOut}(Task{Result{T}}, Func{T, Task{Result{TOut}}})"/>
    /// 串接:第一步失敗就短路,錯誤原封不動往下傳,不必在這裡手動判斷再轉發一次。
    /// Sending and reading are two steps that can each fail, chained with
    /// <see cref="ResultExtensions.ThenAsync{T, TOut}(Task{Result{T}}, Func{T, Task{Result{TOut}}})"/>: a
    /// failure in the first short-circuits and travels on untouched, with no hand-written check-and-forward
    /// in between.
    /// </remarks>
    public Task<Result<string>> SendForStringAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
        SendAsync(request, cancellationToken)
            .ThenAsync(response => ReadBodyAsync(response, cancellationToken));

    private async Task<Result<string>> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await HttpErrorMapper.FromResponseAsync(response, cancellationToken: cancellationToken).ConfigureAwait(false);

                // Retry-After 一併帶進錯誤:呼叫端拿到的是 Result,回應這時已經被釋放,
                // 之後就沒有第二次機會讀那個標頭了。
                // The Retry-After instruction comes along with the error: the caller receives a Result, the
                // response is disposed by then, and there is no second chance to read that header.
                // 回應內容摘要也在錯誤資料裡,對方可能把金鑰 echo 回來 —— 一樣過邊界遮罩。
                // The body snippet sits in the error data too, and a peer may echo the key back in it, so it goes
                // through the boundary masking as well.
                return ErrorSanitizer.Sanitize(HttpErrorMapper.WithRetryAfter(error, response, _timeProvider), _masker);
            }

            try
            {
                return Result.Success(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (HttpRequestException exception)
            {
                return HttpErrorMapper.FromException(exception, _masker);
            }
            catch (OperationCanceledException exception)
            {
                return HttpErrorMapper.FromException(exception, _masker);
            }
        }
    }

    private static Error DescribeCancellation(
        OperationCanceledException exception,
        CancellationTokenSource timeoutSource,
        TimeSpan overallTimeout,
        CancellationToken callerToken)
    {
        // 呼叫端取消與整體逾時都是 OperationCanceledException,但意義完全不同:
        // 前者不該重試也不該告警,後者是真正的故障訊號。用哪個權杖被觸發來分辨。
        // A caller cancellation and an overall timeout are both OperationCanceledException but mean quite
        // different things: the first deserves neither a retry nor an alert, the second is a genuine fault
        // signal. Which token fired is what tells them apart.
        if (callerToken.IsCancellationRequested)
        {
            return HttpErrorMapper.FromException(exception);
        }

        if (timeoutSource.IsCancellationRequested)
        {
            return new Error(
                HttpErrorCodes.Timeout,
                $"整趟請求(含重試)超過 {overallTimeout} 未完成。The exchange, retries included, did not complete within {overallTimeout}.",
                ErrorCategory.Timeout)
            {
                Exception = exception,
            };
        }

        return HttpErrorMapper.FromException(exception);
    }
}

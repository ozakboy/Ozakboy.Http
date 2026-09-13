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
/// <para>
/// <b>門面本身可以是單例,但 <see cref="HttpClient"/> 不可以被長期持有。</b>門面沒有可變狀態 —— 逾時、時間來源、
/// 遮罩器都是唯讀設定,註冊成單例完全沒問題。<see cref="HttpClient"/> 則不同:<see cref="IHttpClientFactory"/>
/// 的處理器輪替(<c>SetHandlerLifetime</c>,預設兩分鐘)只在<b>每一次</b> <c>CreateClient</c> 時才有機會生效。
/// 長期持有同一個 <see cref="HttpClient"/> 等於永遠綁在同一個處理器與它已建立的連線上,對方換 IP 之後 DNS 跟不上 ——
/// 交易所確實會換 IP,而這個症狀只在長時間無人值守的執行中出現,現場看起來就是「跑了一天之後連不上」。
/// 因此以 <see cref="OzakboyHttpServiceProviderExtensions.CreateOzakboyHttpPipelineClient"/> 建立的門面持有的是
/// <see cref="IHttpClientFactory"/> 與用戶端名稱,每一次請求各取一個 <see cref="HttpClient"/>;
/// <c>CreateClient</c> 很便宜,真正昂貴的處理器由 factory 池化與輪替。
/// <b>The facade may be a singleton; an <see cref="HttpClient"/> may not be held for the long term.</b> The
/// facade has no mutable state — timeouts, time source and masker are read-only settings — so registering it as
/// a singleton is fine. An <see cref="HttpClient"/> is another matter: <see cref="IHttpClientFactory"/>'s handler
/// rotation (<c>SetHandlerLifetime</c>, two minutes by default) only gets its chance on <b>each</b>
/// <c>CreateClient</c> call. Holding one client for the long term pins the pipeline to a single handler and the
/// connections it has already established, so DNS never catches up once the peer moves to a new IP — exchanges do
/// move, and the symptom surfaces only on long unattended runs, looking from the outside like "it stopped
/// connecting after a day". A facade built through
/// <see cref="OzakboyHttpServiceProviderExtensions.CreateOzakboyHttpPipelineClient"/> therefore holds the
/// <see cref="IHttpClientFactory"/> and the client name and obtains an <see cref="HttpClient"/> per request;
/// <c>CreateClient</c> is cheap, and the expensive part — the handler — is pooled and rotated by the factory.
/// </para>
/// </remarks>
public sealed class HttpPipelineClient
{
    // 兩條建構路徑二選一:_httpClientFactory 為 null 時用呼叫端自備的 _pinnedClient,
    // 否則每次請求各向 factory 取一個(生命週期與 DNS 輪替的差異見型別說明)。
    // The two construction paths are mutually exclusive: a null _httpClientFactory means the caller's own
    // _pinnedClient is used, otherwise one client is taken from the factory per request. See the type remarks
    // for how the lifetime and DNS rotation differ between them.
    private readonly HttpClient? _pinnedClient;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly string? _clientName;
    private readonly HttpTimeoutOptions _timeouts;
    private readonly TimeProvider _timeProvider;
    private readonly SecretMasker _masker;

    /// <summary>
    /// 以呼叫端自備的 <see cref="HttpClient"/> 建立門面;這個用戶端的生命週期由呼叫端負責。
    /// Creates the facade over an <see cref="HttpClient"/> the caller supplies and whose lifetime the caller owns.
    /// </summary>
    /// <param name="httpClient">
    /// 已組好管線的用戶端。門面會一直用這一個,不會另外取得新的 —— 因此它的生命週期(以及
    /// <see cref="IHttpClientFactory"/> 的處理器輪替跟不跟得上 DNS 變動)完全由呼叫端決定。
    /// 由服務容器註冊的具名用戶端請改用接受 <see cref="IHttpClientFactory"/> 的多載。
    /// The client with the pipeline already assembled. The facade keeps using this one and never obtains another,
    /// so its lifetime — and therefore whether <see cref="IHttpClientFactory"/>'s handler rotation can keep up
    /// with DNS changes — is entirely the caller's business. For a named client registered in a service
    /// container, use the overload taking an <see cref="IHttpClientFactory"/> instead.
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
    /// 以呼叫端自備的 <see cref="HttpClient"/> 建立門面,並指定錯誤邊界使用的遮罩器;這個用戶端的生命週期由呼叫端負責。
    /// Creates the facade over a caller-supplied <see cref="HttpClient"/>, whose lifetime the caller owns, with the
    /// masker its error boundary uses.
    /// </summary>
    /// <param name="httpClient">
    /// 已組好管線的用戶端。門面會一直用這一個,不會另外取得新的;生命週期與 DNS 輪替由呼叫端負責。
    /// The client with the pipeline already assembled. The facade keeps using this one and never obtains another;
    /// its lifetime, and DNS rotation with it, is the caller's responsibility.
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

        _pinnedClient = httpClient;
        _timeouts = ValidateTimeouts(timeouts);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _masker = masker ?? SecretMasker.Default;
    }

    /// <summary>
    /// 以 <see cref="IHttpClientFactory"/> 與具名用戶端建立門面:每一次請求各取一個 <see cref="HttpClient"/>,
    /// 讓 factory 的處理器輪替得以生效。門面本身可以是單例。
    /// Creates the facade from an <see cref="IHttpClientFactory"/> and a client name, obtaining one
    /// <see cref="HttpClient"/> per request so that the factory's handler rotation can take effect. The facade
    /// itself may be a singleton.
    /// </summary>
    /// <param name="httpClientFactory">用戶端工廠。The client factory.</param>
    /// <param name="clientName">
    /// 用戶端名稱,與 <c>AddHttpClient</c> 時相同。The client name used with <c>AddHttpClient</c>.
    /// </param>
    /// <param name="timeouts">逾時設定;<see langword="null"/> 時使用預設值。The timeout options; defaults are used when <see langword="null"/>.</param>
    /// <param name="timeProvider">時間來源;測試請傳入假時鐘。The time source; tests pass a fake clock.</param>
    /// <param name="masker">
    /// 遮罩器,通常是 <see cref="OzakboyHttpServiceProviderExtensions.GetOzakboyHttpMasker"/> 取得的那一個;
    /// <see langword="null"/> 時使用 <see cref="SecretMasker.Default"/>。
    /// The masker, usually the one from <see cref="OzakboyHttpServiceProviderExtensions.GetOzakboyHttpMasker"/>;
    /// <see cref="SecretMasker.Default"/> when <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// 這條路徑與接受 <see cref="HttpClient"/> 的多載差別只有一處,但後果不小:門面不持有 <see cref="HttpClient"/>,
    /// 每次請求各取一個,因此 <c>SetHandlerLifetime</c>(預設兩分鐘)的處理器輪替才有機會發生,對方換 IP 之後 DNS 跟得上。
    /// 手動傳入 <see cref="HttpClient"/> 的那條路徑會一直用同一個,長期持有等於永遠綁在同一組連線上。
    /// 由服務容器建立時請直接用 <see cref="OzakboyHttpServiceProviderExtensions.CreateOzakboyHttpPipelineClient"/>,
    /// 它會連同這個用戶端的遮罩器與同一份逾時設定一起帶入。
    /// This path differs from the <see cref="HttpClient"/> overload in one respect with sizeable consequences: the
    /// facade holds no <see cref="HttpClient"/> and takes one per request, so handler rotation under
    /// <c>SetHandlerLifetime</c> (two minutes by default) actually gets to happen and DNS keeps up when the peer
    /// moves to a new IP. The hand-supplied-client path keeps using the one it was given, which over a long run
    /// pins it to one set of connections. From a service container, prefer
    /// <see cref="OzakboyHttpServiceProviderExtensions.CreateOzakboyHttpPipelineClient"/>, which also brings in
    /// that client's masker and the same timeout settings.
    /// <para>
    /// <b>處理器鏈在這裡就建起來,不等到第一次請求。</b>建構時先向工廠取一個用戶端隨即丟掉,唯一的目的是讓管線
    /// 自己的服務(例如以用戶端名稱為鍵、由容器持有的限流器)在「這個時間點」被建立。服務容器的釋放順序是
    /// 建立順序的反序,「相依者先於它所相依的東西被釋放」全靠這一點:處理器鏈若拖到第一次請求才建,
    /// 限流器就會比用它送請求的服務更晚進到容器的待釋放清單,關機時反而先被釋放。後果是任何在自己的
    /// <c>DisposeAsync</c> 裡送出收尾請求的服務都會拿到 <see cref="ObjectDisposedException"/> ——
    /// 幣安使用者資料串流收尾時要 <c>DELETE</c> 掉 listenKey 就是一例,那把串流憑證會因此留到自然過期。
    /// 這種症狀只在關機路徑上出現,平常怎麼跑都正常。
    /// <b>The handler chain is built here, not at the first request.</b> A client is taken from the factory at
    /// construction and immediately dropped, for one purpose: to have the pipeline's own services — such as the
    /// container-held limiter keyed by client name — created at <i>this</i> point. A service container disposes in
    /// reverse order of creation, and that is the whole basis for "a dependant is disposed before what it depends
    /// on": deferring the handler chain to the first request would put the limiter into the container's disposal
    /// list later than the service sending requests through it, so at shutdown the limiter would go first. Any
    /// service that sends a farewell request from its own <c>DisposeAsync</c> would then get an
    /// <see cref="ObjectDisposedException"/> — the Binance user data stream's <c>DELETE</c> of its listenKey is one,
    /// and the stream credential would be left to lapse on its own. The symptom appears only on the shutdown path;
    /// everything looks fine while running.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpClientFactory"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="httpClientFactory"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="clientName"/> 為 <see langword="null"/> 或空白,或逾時設定不合法時擲出。
    /// Thrown when <paramref name="clientName"/> is <see langword="null"/> or blank, or the timeout options are
    /// invalid.
    /// </exception>
    public HttpPipelineClient(
        IHttpClientFactory httpClientFactory,
        string clientName,
        HttpTimeoutOptions? timeouts = null,
        TimeProvider? timeProvider = null,
        SecretMasker? masker = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        _httpClientFactory = httpClientFactory;
        _clientName = clientName;
        _timeouts = ValidateTimeouts(timeouts);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _masker = masker ?? SecretMasker.Default;

        // 這裡取一個用戶端隨即丟掉,要的不是用戶端,而是逼工廠「現在」就把這個具名用戶端的處理器鏈建起來。
        // 理由是釋放順序,見這個建構式的說明。
        // A client is taken and dropped here: what is wanted is not the client but the handler chain, built now
        // rather than at the first request. The reason is disposal order; see this constructor's remarks.
        _ = ResolveHttpClient();
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

        // 每一次請求各取一個用戶端(工廠路徑),處理器輪替才有機會發生;取得的用戶端刻意不釋放:
        // 工廠給的用戶端本來就不需要釋放(昂貴的處理器由工廠池化),釋放它也不會歸還或關閉那個處理器。
        // One client per request on the factory path, which is what gives handler rotation its chance. The client
        // obtained is deliberately not disposed: a factory-provided client needs no disposal — the expensive part,
        // the handler, is pooled by the factory — and disposing it would neither return nor close that handler.
        var httpClient = ResolveHttpClient();

        using var timeoutSource = new CancellationTokenSource(_timeouts.OverallTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            return await httpClient.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (ResultException exception) when (
            exception.Error.Category == ErrorCategory.Cancelled
            && timeoutSource.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            // 請求還在限流器前排隊時整體逾時到了。限流器看到的是權杖被取消、回報「取消」,
            // 但呼叫端什麼都沒取消 —— 這是整體逾時,必須照整體逾時回報,否則既不重試也不告警。
            // The overall timeout expired while the request was still queueing at the limiter. The limiter saw its
            // token cancelled and reported a cancellation, but the caller cancelled nothing: this is the overall
            // timeout and must be reported as one, or it would be neither retried nor alerted on.
            return Result.Failure<HttpResponseMessage>(ErrorSanitizer.Sanitize(OverallTimedOut(exception, _timeouts.OverallTimeout), _masker));
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

    private HttpClient ResolveHttpClient() =>
        _httpClientFactory is null
            ? _pinnedClient!
            : _httpClientFactory.CreateClient(_clientName!);

    private static HttpTimeoutOptions ValidateTimeouts(HttpTimeoutOptions? timeouts)
    {
        var effectiveTimeouts = timeouts ?? new HttpTimeoutOptions();
        var validation = effectiveTimeouts.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(timeouts));
        }

        return effectiveTimeouts;
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
            return OverallTimedOut(exception, overallTimeout);
        }

        return HttpErrorMapper.FromException(exception);
    }

    private static Error OverallTimedOut(Exception cause, TimeSpan overallTimeout) =>
        new(
            HttpErrorCodes.Timeout,
            $"整趟請求(含限流等待與重試)超過 {overallTimeout} 未完成。The exchange, rate-limit waits and retries included, did not complete within {overallTimeout}.",
            ErrorCategory.Timeout)
        {
            Exception = cause,
        };
}

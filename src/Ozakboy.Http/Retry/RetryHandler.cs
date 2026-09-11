using System.Globalization;
using Microsoft.Extensions.Options;
using Ozakboy.Core.Abstractions;
using Ozakboy.Security.Masking;

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
/// <para>
/// <b>位置在管線最外層。</b>限流、簽章、日誌都在它之內,所以每一次嘗試都會重新付權重、重新簽章(新的時間戳)、
/// 各自留下一筆日誌。退避等待發生在這一層,那時內層的限流器不持有任何許可。0.2.0 把它放在簽章與限流之內,
/// 重試因此沿用舊的時間戳、也不付權重,見 <see cref="OzakboyHttpClientBuilderExtensions"/>。
/// <b>It sits outermost in the pipeline.</b> Rate limiting, signing and logging are all inside it, so every
/// attempt pays its own weight, is re-signed with a fresh timestamp, and leaves its own log line. Backoff waits
/// happen at this level, while the inner limiter holds no permit. In 0.2.0 it sat inside signing and rate
/// limiting, so retries reused the old timestamp and paid no weight; see
/// <see cref="OzakboyHttpClientBuilderExtensions"/>.
/// </para>
/// <para>
/// <b>單次嘗試逾時從拿到限流許可、開始送出時才起算。</b>限流處理器等待許可期間,這個計時會暫停,拿到許可後從頭計時
/// (見 <see cref="Ozakboy.Http.RateLimiting.RateLimitingHandler"/>)。單次逾時量的是「對方多久沒回應」;
/// 排隊若也算進去,單次逾時(幣安預設 10 秒)短於限流等待上限(30 秒)時,排隊超過 10 秒的請求就會逾時、被重試、
/// 重新排到隊伍最後面。排隊仍受限流器自己的上限與呼叫端的整體逾時約束。
/// <b>The attempt timeout starts only once the rate-limit permit is held and the request goes out.</b> While
/// the rate-limiting handler waits for permits the clock is paused, and it restarts from zero once they are
/// granted (see <see cref="Ozakboy.Http.RateLimiting.RateLimitingHandler"/>). The attempt timeout measures how
/// long the peer takes to answer; counting the queue as well would, whenever the attempt timeout (10 seconds by
/// Binance's default) is shorter than the limiter's ceiling (30 seconds), time out any request that queued past
/// 10 seconds and retry it at the back of the queue. The queue remains bounded by the limiter's own ceiling and
/// the caller's overall timeout.
/// </para>
/// <para>
/// <b>本地限流逾時(<see cref="HttpErrorCodes.RateLimitTimeout"/>)預設不重試。</b>那個請求從未送出,而且已經等滿了
/// 限流器的等待上限;再試一次只會把同樣長的等待乘上嘗試次數。策略設了 <see cref="RetryPolicy.RetryPredicate"/>
/// 時由它決定。本處理器產生的錯誤不攜帶原始例外物件,一律換成 <see cref="SanitizedException"/>。
/// <b>A local rate-limit timeout (<see cref="HttpErrorCodes.RateLimitTimeout"/>) is not retried by
/// default.</b> The request never went out and has already waited out the limiter's full ceiling; another
/// attempt only multiplies that wait by the attempt count. A policy with a
/// <see cref="RetryPolicy.RetryPredicate"/> decides for itself. Errors raised here never carry the original
/// exception object; it is always swapped for a <see cref="SanitizedException"/>.
/// </para>
/// </remarks>
public sealed class RetryHandler : DelegatingHandler
{
    private readonly RetryOptions _options;
    private readonly HttpTimeoutOptions _timeouts;
    private readonly TimeProvider _timeProvider;
    private readonly SecretMasker _masker;

    /// <summary>
    /// 建立重試處理器,錯誤以 <see cref="SecretMasker.Default"/> 遮罩。
    /// Creates the retry handler, masking errors with <see cref="SecretMasker.Default"/>.
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
        : this(options, timeouts, timeProvider, null)
    {
    }

    /// <summary>
    /// 建立重試處理器,錯誤以指定的遮罩器遮罩。
    /// Creates the retry handler, masking errors with the given masker.
    /// </summary>
    /// <param name="options">重試設定。The retry options.</param>
    /// <param name="timeouts">逾時設定;<see langword="null"/> 時使用預設值。The timeout options; defaults are used when <see langword="null"/>.</param>
    /// <param name="timeProvider">時間來源;測試請傳入假時鐘。The time source; tests pass a fake clock.</param>
    /// <param name="masker">
    /// 遮罩器;<see langword="null"/> 時使用 <see cref="SecretMasker.Default"/>。
    /// The masker; <see cref="SecretMasker.Default"/> when <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定不合法時擲出。Thrown when the options are invalid.
    /// </exception>
    public RetryHandler(RetryOptions options, HttpTimeoutOptions? timeouts, TimeProvider? timeProvider, SecretMasker? masker)
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
        _masker = masker ?? SecretMasker.Default;
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
                try
                {
                    var response = await SendAttemptAsync(attemptRequest, cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return response;
                    }

                    var mapped = _options.ErrorBodySnippetLength > 0
                        ? await HttpErrorMapper.FromResponseAsync(response, _options.ErrorBodySnippetLength, cancellationToken).ConfigureAwait(false)
                        : HttpErrorMapper.FromStatusCode(response.StatusCode, response.ReasonPhrase);

                    // Retry-After 先寫進錯誤,再問策略。順序是重點:策略的 RetryPredicate 只看得到 Error,
                    // 而「這個 429 帶不帶 Retry-After」正是呼叫端最想用來分辨「稍後再來」與「位址被封」的依據。
                    // 先寫進去,那個判斷才寫得進策略裡,不必在這個處理器中另開一層規則。
                    // The Retry-After instruction goes into the error before the policy is asked, and the
                    // order is the point: a RetryPredicate only ever sees an Error, and whether a 429 carries
                    // Retry-After is exactly what tells "come back later" from "this address is banned".
                    // Writing it in first is what lets that judgement live in the policy instead of growing a
                    // second layer of rules in this handler.
                    var error = HttpErrorMapper.WithRetryAfter(mapped, response, _timeProvider);

                    if (!ShouldRetry(policy, attempt, error))
                    {
                        // 次數用盡也照樣把回應交還出去 —— 真實的狀態碼與內容比一個合成的錯誤有用,
                        // 而「這是不是最後一次」由呼叫端自己的重試層去判斷。
                        // The response goes back even when the attempts are spent: the real status and body
                        // are worth more than a synthesised error, and whether this was the last word is for
                        // the caller's own retry layer to decide.
                        return response;
                    }

                    if (_options.RespectRetryAfter && error.TryGetDecimal(HttpErrorDataKeys.RetryAfterSeconds, out var seconds))
                    {
                        var after = TimeSpan.FromSeconds((double)seconds);
                        retryAfter = after > _options.MaxRetryAfter ? _options.MaxRetryAfter : after;
                    }

                    response.Dispose();
                }
                catch (ResultException exception)
                {
                    if (!ShouldRetry(policy, attempt, exception.Error))
                    {
                        if (IsWorthRetrying(policy, exception.Error))
                        {
                            throw Exhaust(ErrorSanitizer.Sanitize(exception.Error, _masker), attempt).ToException();
                        }

                        throw;
                    }
                }
                catch (HttpRequestException exception)
                {
                    var error = HttpErrorMapper.FromException(exception, _masker);
                    if (!ShouldRetry(policy, attempt, error))
                    {
                        if (IsWorthRetrying(policy, error))
                        {
                            throw Exhaust(error, attempt).ToException();
                        }

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

    /// <summary>
    /// 問同一份策略「這個錯誤本身值不值得重試」,與次數無關。
    /// Asks the same policy whether this error is worth retrying at all, independent of the attempt count.
    /// </summary>
    /// <remarks>
    /// 刻意以 <c>attempt = 1</c> 呼叫 <see cref="RetryPolicy.ShouldRetry(int, Error)"/>,而不是在這裡照抄一份
    /// 「<see cref="RetryPolicy.RetryPredicate"/> 有就用它、沒有就看 <see cref="Error.IsTransient"/>」的規則:
    /// 走到這裡時 <see cref="RetryPolicy.MaxAttempts"/> 必定大於 1(否則早就走了不重試的分支),
    /// 因此答案只反映錯誤本身。抄一份規則就會有兩個真相來源,而策略那邊改了這裡不會有人發現。
    /// This deliberately calls <see cref="RetryPolicy.ShouldRetry(int, Error)"/> with <c>attempt = 1</c>
    /// rather than restating the "use <see cref="RetryPolicy.RetryPredicate"/> if set, otherwise
    /// <see cref="Error.IsTransient"/>" rule here. By this point <see cref="RetryPolicy.MaxAttempts"/> is
    /// necessarily above 1 — the no-retry branch was taken otherwise — so the answer reflects only the error.
    /// A copied rule would be a second source of truth, and a change on the policy side would go unnoticed here.
    /// </remarks>
    private static bool IsWorthRetrying(RetryPolicy policy, Error error) => ShouldRetry(policy, 1, error);

    /// <summary>
    /// 問策略這次該不該重試,並套用本處理器唯一的額外規則:本地限流逾時預設不重試。
    /// Asks the policy whether to retry, applying this handler's single extra rule: a local rate-limit timeout
    /// is not retried by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HttpErrorCodes.RateLimitTimeout"/> 的分類是 <see cref="ErrorCategory.RateLimited"/>(暫時性),
    /// 對「稍後再試」的呼叫端而言沒錯;但在重試處理器裡,「稍後」就是立刻再排一次同樣長的隊。
    /// 限流在重試之內,這條規則不寫在這裡,預設策略就會把等待乘上嘗試次數。
    /// <see cref="HttpErrorCodes.RateLimitTimeout"/> is categorised <see cref="ErrorCategory.RateLimited"/>, which is
    /// transient — right for a caller deciding to come back later, but inside the retry handler "later" means
    /// queueing just as long again straight away. With rate limiting inside retry, the default policy would
    /// multiply the wait by the attempt count without this rule.
    /// </para>
    /// <para>
    /// 設了 <see cref="RetryPolicy.RetryPredicate"/> 的策略完全由它決定,與策略本身「取代而非附加」的語意一致。
    /// A policy with a <see cref="RetryPolicy.RetryPredicate"/> is left entirely to it, in keeping with the
    /// policy's own "replace, not add" semantics.
    /// </para>
    /// </remarks>
    private static bool ShouldRetry(RetryPolicy policy, int attempt, Error error)
    {
        if (policy.RetryPredicate is null && string.Equals(error.Code, HttpErrorCodes.RateLimitTimeout, StringComparison.Ordinal))
        {
            return false;
        }

        return policy.ShouldRetry(attempt: attempt, error: error);
    }

    /// <summary>
    /// 把「值得重試但次數已經用盡」的錯誤改標為 <see cref="ErrorCategory.Exhausted"/>。
    /// Re-labels an error that was worth retrying but ran out of attempts as
    /// <see cref="ErrorCategory.Exhausted"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 原本的分類(逾時、連線失敗)是暫時性的,交回給呼叫端時就等於說「再試一次可能會過」——
    /// 但我們已經替它試過了。呼叫端的重試層看到暫時性就再跑一輪,於是同一個故障被乘上兩層次數。
    /// <see cref="ErrorCategory.Exhausted"/> 的 <see cref="Error.IsTransient"/> 為 <see langword="false"/>,
    /// 正是「原本可行,但機會已用盡」這句話。
    /// The original category — a timeout, a dropped connection — is transient, and handing it back says
    /// "another attempt might work"; but the attempts have already been made. A caller's own retry layer sees
    /// transient and runs another round, multiplying one fault by two layers of attempt counts.
    /// <see cref="ErrorCategory.Exhausted"/> reports <see cref="Error.IsTransient"/> as
    /// <see langword="false"/>, which is precisely "it could have worked, but the attempts are spent".
    /// </para>
    /// <para>
    /// 代碼、訊息、內層例外(已是遮罩後的 <see cref="SanitizedException"/>)與既有資料一律保留 ——
    /// 綁在 <see cref="HttpErrorCodes"/> 上分支的呼叫端不受影響,變的只有分類與多出來的 <see cref="HttpErrorDataKeys.Attempts"/>。
    /// The code, message, inner exception (already a masked <see cref="SanitizedException"/>), and existing data
    /// are all kept, so callers branching on
    /// <see cref="HttpErrorCodes"/> are unaffected; only the category changes, plus the added
    /// <see cref="HttpErrorDataKeys.Attempts"/> entry.
    /// </para>
    /// </remarks>
    private static Error Exhaust(Error error, int attempts) =>
        (Error.Exhausted(
            error.Code,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{error.Message}(已嘗試 {attempts} 次仍失敗,不再重試。Gave up after {attempts} attempts.)")) with
        {
            Exception = error.Exception,
            Data = error.Data,
        }).WithData(HttpErrorDataKeys.Attempts, attempts);

    private static bool IsSafeMethod(HttpMethod method) =>
        method == HttpMethod.Get
        || method == HttpMethod.Head
        || method == HttpMethod.Options
        || method == HttpMethod.Trace;

    private async Task<HttpResponseMessage> SendAttemptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 計時器掛在這次嘗試的請求上,內層的限流處理器等待許可時會暫停它、拿到許可後從頭起算(見型別說明)。
        // The timer rides on this attempt's request; the inner rate-limiting handler pauses it while waiting for
        // permits and restarts it once they are granted (see the type remarks).
        using var timer = new AttemptTimer(_timeouts.AttemptTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        AttemptTimer.Attach(request, timer);

        try
        {
            return await base.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (timer.HasExpired && !cancellationToken.IsCancellationRequested)
        {
            throw AttemptTimedOut(exception).ToException();
        }
        finally
        {
            AttemptTimer.Detach(request);
        }
    }

    private Error AttemptTimedOut(Exception cause) =>
        new(
            HttpErrorCodes.AttemptTimeout,
            $"單次嘗試超過 {_timeouts.AttemptTimeout} 未完成。A single attempt did not complete within {_timeouts.AttemptTimeout}.",
            ErrorCategory.Timeout)
        {
            Exception = ErrorSanitizer.Sanitize(cause, _masker),
        };
}

using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http.Signing;

/// <summary>
/// 把請求攜帶的參數編碼進 URI(或請求主體),需要時再附上簽章與 API 金鑰標頭。
/// Encodes the request's parameters into the URI (or body) and, when required, appends the signature and the
/// API-key header.
/// </summary>
/// <remarks>
/// <para>
/// <b>位置在重試與限流之內、日誌之外。</b>每一次嘗試都是一個新請求,必須重新簽章;而且要在拿到限流許可<b>之後</b>才簽 ——
/// 時間戳必須是送出那一刻的(見 <see cref="SigningOptions.TimestampParameterName"/>)。先簽再排隊,時間戳就在隊伍裡過期:
/// 限流等待上限預設 30 秒,幣安的 recvWindow 預設只有 5 秒。簽章之後只剩日誌,不改動請求內容,
/// 送出的字串因此與簽過的字串逐字相同。
/// <b>It sits inside retry and rate limiting, outside logging.</b> Every attempt is a new request and has to be
/// signed afresh — and only <b>after</b> the rate-limit permit is held, because the timestamp must be the moment
/// the request goes out (see <see cref="SigningOptions.TimestampParameterName"/>). Sign first and queue
/// afterwards, and the timestamp ages in the queue: the limiter waits up to 30 seconds by default, while
/// Binance's recvWindow defaults to 5. Only logging follows, and it never alters the request, so the string sent
/// matches the string signed byte for byte.
/// </para>
/// <para>
/// 歷史:0.2.0 把它放在最外層、重試之外,重試因此沿用第一次的簽章與時間戳(當時的註解宣稱這是刻意的,推理剛好相反)。
/// 0.3.0 開發中先改成「重試 → 簽章 → 限流」,重試是修好了,但變成先簽章再排隊,排隊超過 recvWindow 的請求一出去就被拒絕
/// (<c>-1021</c>),所以最後定在限流之內。
/// History: 0.2.0 put it outermost, outside retry, so retries reused the first attempt's signature and
/// timestamp (the comment of the time called that deliberate, with the reasoning exactly backwards). A 0.3.0
/// draft moved to "retry, signing, rate limiting", which fixed retries but signed before queueing, so a request
/// that queued past recvWindow was rejected the moment it went out (<c>-1021</c>); hence its final place inside
/// rate limiting.
/// </para>
/// <para>
/// 未標記需要簽章、但帶有參數的請求,仍會把參數編碼進 URI。這樣公開端點與私有端點的組裝方式一致,
/// 呼叫端不需要為了「這支不用簽」而換一套寫法。
/// A request that carries parameters but is not marked for signing still has those parameters encoded into the
/// URI, so public and private endpoints are assembled the same way and callers need no second style for
/// "this one is unsigned".
/// </para>
/// <para>
/// 關掉簽章的管線(<see cref="HttpPipelineOptions.EnableSigning"/> 為 <see langword="false"/>)不掛這個處理器,
/// 由內部的 <see cref="QueryParametersHandler"/> 在同一個位置把參數寫進位址。0.3.2 以前沒有那一段,
/// 未簽章管線的 query 參數因此完全沒有送出。
/// A pipeline with signing switched off (<see cref="HttpPipelineOptions.EnableSigning"/> set to
/// <see langword="false"/>) does not attach this handler; the internal <see cref="QueryParametersHandler"/> writes
/// the parameters into the URI from the same position instead. Up to 0.3.2 nothing took that place, and an
/// unsigned pipeline sent none of its query parameters at all.
/// </para>
/// </remarks>
public sealed class SigningHandler : DelegatingHandler
{
    private readonly SigningOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 以設定建立處理器,時間來源為 <see cref="TimeProvider.System"/>。
    /// Creates the handler from options, using <see cref="TimeProvider.System"/> as the time source.
    /// </summary>
    /// <param name="options">簽章設定。The signing options.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定不合法時擲出。Thrown when the options are invalid.
    /// </exception>
    public SigningHandler(SigningOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// 以設定與時間來源建立處理器。
    /// Creates the handler from options and a time source.
    /// </summary>
    /// <param name="options">簽章設定。The signing options.</param>
    /// <param name="timeProvider">
    /// 蓋時間戳用的時間來源(見 <see cref="SigningOptions.TimestampParameterName"/>);<see langword="null"/> 時使用
    /// <see cref="TimeProvider.System"/>。
    /// The time source for restamping (see <see cref="SigningOptions.TimestampParameterName"/>);
    /// <see cref="TimeProvider.System"/> when <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定不合法時擲出。Thrown when the options are invalid.
    /// </exception>
    public SigningHandler(SigningOptions options, TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);

        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 以選項容器建立處理器,供相依性注入使用。
    /// Creates the handler from an options container, for dependency injection.
    /// </summary>
    /// <param name="options">簽章設定容器。The signing options container.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    public SigningHandler(IOptions<SigningOptions> options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value)
    {
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request.GetQueryParameters();
        var requiresSignature = request.RequiresSignature();

        if (parameters is null && !requiresSignature)
        {
            return base.SendAsync(request, cancellationToken);
        }

        if (requiresSignature && _options.TimestampParameterName is { } timestampName)
        {
            // 每次嘗試都蓋上當下時間:重試處理器在外層,每一次嘗試都會重新走到這裡。
            // 請求選項裡的參數是不可變的,這裡產生新實例,原請求(重試複製的來源)不受影響。
            // Stamped with the current time on every attempt: retry sits outside, so every attempt passes
            // through here again. The parameters in the request options are immutable; a new instance is made
            // here and the original request, which retries clone from, is left alone.
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            parameters = (parameters ?? QueryParameters.Empty)
                .WithValue(timestampName, now.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var canonical = (parameters ?? QueryParameters.Empty).ToQueryString();
        var payload = canonical;

        if (requiresSignature)
        {
            var signature = _options.Algorithm.Sign(canonical, _options.SecretKey);
            if (!signature.TryGetValue(out var value))
            {
                throw signature.Error!.ToException();
            }

            // 簽章參數本身也要編碼,而且必須接在待簽字串「之後」——
            // 它不是簽章的輸入,順序放錯會讓對方算出不同的簽章。
            // The signature parameter is encoded too and must come after the canonical string: it is not an
            // input to the signature, and putting it elsewhere makes the peer compute a different one.
            payload = canonical.Length == 0
                ? $"{Uri.EscapeDataString(_options.SignatureParameterName)}={value}"
                : $"{canonical}&{Uri.EscapeDataString(_options.SignatureParameterName)}={value}";

            if (_options.SendApiKeyHeader && !string.IsNullOrEmpty(_options.ApiKey))
            {
                // TryAddWithoutValidation:金鑰標頭是自訂標頭,不必也不該走 BCL 的已知標頭驗證。
                // TryAddWithoutValidation: the key header is a custom header and need not go through the
                // BCL's validation for well-known headers.
                request.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);
            }
        }

        if (_options.Placement == SignedPayloadPlacement.FormBody && requiresSignature)
        {
            request.Content?.Dispose();
            request.Content = new StringContent(payload, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded")
            {
                CharSet = "utf-8",
            };
        }
        else
        {
            // 與未簽章管線的 QueryParametersHandler 共用同一份寫入規則,兩邊送出的位址才會逐字一致。
            // Shares its write rule with QueryParametersHandler on unsigned pipelines, so both send the same URI.
            request.RequestUri = RequestUriQuery.Replace(request.RequestUri, payload);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

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
/// <b>位置在重試之內、限流之外。</b>每一次嘗試都是一個新請求,必須重新簽章:時間戳要是當下的
/// (見 <see cref="SigningOptions.TimestampParameterName"/>),否則退避一久,重試就會被對方的時間窗拒絕
/// (幣安 <c>-1021</c>)。簽章之後的處理器(限流、日誌)都不改動請求內容,送出的字串因此與簽過的字串逐字相同。
/// 0.2.0 把它放在最外層、重試之外,重試因此沿用第一次的簽章與時間戳 —— 當時的註解宣稱這是刻意的,推理剛好相反。
/// <b>It sits inside retry and outside rate limiting.</b> Every attempt is a new request and has to be signed
/// afresh with a current timestamp (see <see cref="SigningOptions.TimestampParameterName"/>); otherwise a long
/// backoff gets the retry rejected by the peer's time window (Binance <c>-1021</c>). The handlers after it —
/// rate limiting and logging — never alter the request, so the string sent matches the string signed byte for
/// byte. In 0.2.0 it sat outermost, outside retry, so retries reused the first attempt's signature and
/// timestamp; the comment of the time called that deliberate, with the reasoning exactly backwards.
/// </para>
/// <para>
/// 未標記需要簽章、但帶有參數的請求,仍會把參數編碼進 URI。這樣公開端點與私有端點的組裝方式一致,
/// 呼叫端不需要為了「這支不用簽」而換一套寫法。
/// A request that carries parameters but is not marked for signing still has those parameters encoded into the
/// URI, so public and private endpoints are assembled the same way and callers need no second style for
/// "this one is unsigned".
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
            request.RequestUri = ReplaceQuery(request.RequestUri, payload);
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// 換掉位址的 query 部分,保留 scheme、authority 與路徑。
    /// Replaces the query part of a URI, keeping the scheme, authority, and path.
    /// </summary>
    /// <param name="uri">原位址,允許相對位址。The original URI; relative URIs are accepted.</param>
    /// <param name="query">已編碼的 query 字串(不含 <c>?</c>)。The encoded query string, without the <c>?</c>.</param>
    /// <returns>換好 query 的位址。The URI with the new query.</returns>
    /// <remarks>
    /// 刻意不用 <see cref="UriBuilder"/>:它會重新組裝位址,而我們需要的是送出的字串與簽過的字串
    /// 逐字相同。這裡以字串層級直接接上,而 <see cref="Uri.EscapeDataString(string)"/> 不會編碼未保留字元,
    /// 因此 <see cref="Uri"/> 也不會把我們的 <c>%XX</c> 還原回去。
    /// <see cref="UriBuilder"/> is avoided on purpose: it reassembles the URI, and what this needs is a string
    /// that matches the signed one exactly. The query is concatenated at the string level instead, and since
    /// <see cref="Uri.EscapeDataString(string)"/> never escapes unreserved characters, <see cref="Uri"/> has
    /// nothing of ours to unescape back.
    /// </remarks>
    private static Uri ReplaceQuery(Uri? uri, string query)
    {
        if (uri is null)
        {
            throw Error.Validation(
                HttpErrorCodes.SigningMissingRequestUri,
                "請求沒有目標位址,無法附加參數。The request has no target URI, so parameters cannot be attached.").ToException();
        }

        var text = uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Path) : uri.OriginalString;
        var separator = text.IndexOf('?', StringComparison.Ordinal);
        if (separator >= 0)
        {
            text = text[..separator];
        }

        var target = query.Length == 0 ? text : $"{text}?{query}";
        return new Uri(target, uri.IsAbsoluteUri ? UriKind.Absolute : UriKind.Relative);
    }
}

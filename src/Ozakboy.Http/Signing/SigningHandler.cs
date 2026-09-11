using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http.Signing;

/// <summary>
/// 管線的最外層:把請求攜帶的參數編碼進 URI(或請求主體),需要時再附上簽章與 API 金鑰標頭。
/// The outermost handler: encodes the request's parameters into the URI (or body) and, when required, appends
/// the signature and the API-key header.
/// </summary>
/// <remarks>
/// <para>
/// 放在最外層是刻意的。簽章必須是最後才決定的內容之外的一切都已定案 —— 如果限流或重試在簽章之後
/// 才改動請求,送出的字串就會與簽過的字串不一致。
/// The position is deliberate. Everything that goes into the signature must already be settled: if rate
/// limiting or retry modified the request after signing, the string sent would no longer match the string
/// signed.
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

    /// <summary>
    /// 以設定建立處理器。
    /// Creates the handler from options.
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
    {
        ArgumentNullException.ThrowIfNull(options);

        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        _options = options;
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

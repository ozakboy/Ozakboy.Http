# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-09-11

Upgrades to `Ozakboy.Core.Abstractions` 0.3.0 and takes up what that version added — APIs that exist
because this package reported the gaps in the first place. Nothing here is a rename: the custom exception this package defined to
work around a missing type is gone, and the judgements that had to live in a handler now live in the policy.

升級到 `Ozakboy.Core.Abstractions` 0.3.0,並實際用上該版新增的 API —— 這些 API 全部源自本套件當初回報的缺口。
這一版不是改名而已:當初為了繞過缺少型別而自訂的例外已經移除,原本只能寫在處理器裡的判斷也搬回了策略。

### Removed

- **`HttpPipelineException`.** Replaced everywhere by `ResultException` from `Ozakboy.Core.Abstractions`.
  The type only ever existed because there was no official way to carry an `Error` across a boundary whose
  signature belongs to the BCL — `DelegatingHandler.SendAsync` must return `Task<HttpResponseMessage>`, and a
  `Result<T>` cannot cross that. Every package solving it privately means consumers catching N exception types
  to recover one `Error`, each with its own retrieval convention. `ResultException` is that single carrier: it
  derives from `InvalidOperationException`, and its `Error` property is never null. Callers that used a bare
  `HttpClient` and caught `HttpPipelineException` catch `ResultException` instead; those going through
  `HttpPipelineClient` see no difference at all, since both come back as `Result<T>`.
  移除 `HttpPipelineException`,全面改用 `ResultException`。統一的例外邊界型別讓消費端不必為了取回一個
  `Error` 而攔 N 種例外。

### Added

- **`HttpErrorDataKeys`.** The keys this package writes into `Error.Data`: `statusCode`, `body`,
  `retryAfterSeconds`, `attempts`. They are part of the public contract, like `HttpErrorCodes`.
  本套件寫進 `Error.Data` 的資料鍵常數,與 `HttpErrorCodes` 一樣屬於公開契約。

- **`HttpErrorMapper.WithRetryAfter`.** Writes the peer's `Retry-After` instruction into the error's data.
  This is what lets the retry decision stay entirely inside the policy: a `RetryPolicy.RetryPredicate` only
  ever sees an `Error`, never the response, so without this the "a 429 with `Retry-After` is worth retrying,
  one without usually means the address is banned" judgement had nowhere to live but inside a handler.
  把對方的 `Retry-After` 指示寫進錯誤資料,重試判斷才能完全留在策略裡。

### Changed

- **Error data is typed on both sides.** `HttpErrorMapper` builds errors with `Error.WithData` instead of
  assembling a `Dictionary` and `with`-ing it in, and the status code goes in as a number rather than a
  string. Consumers read it back with `TryGetInt64` / `TryGetDecimal` / `TryGetData`, so nobody parses a
  string at the far end and nobody gets the chance to forget `InvariantCulture`.
  錯誤資料兩端都型別化:狀態碼與 `Retry-After` 秒數存成數值,消費端用 `TryGetInt64` / `TryGetDecimal` 讀回。

- **Retries that run out now report `ErrorCategory.Exhausted`.** When a failure was worth retrying but the
  attempts are spent, the error carried out of `RetryHandler` keeps its code, message and inner exception but
  changes category, so `IsTransient` becomes false and a caller's own retry layer does not multiply the same
  fault by another round. The number of attempts made is attached as `HttpErrorDataKeys.Attempts`. A failure
  that was never worth retrying is untouched — labelling it exhausted would claim attempts that never
  happened. **Behaviour change:** a transport failure that exhausts its retries now surfaces as
  `ResultException` rather than the original `HttpRequestException`, which is preserved as the inner
  exception. Getting a response back is unaffected: an exhausted status-code failure still returns the real
  response, because the actual status is worth more than a synthesised error.
  重試次數用盡時改標為 `ErrorCategory.Exhausted`(代碼與訊息保留),呼叫端的重試層才不會再跑一輪。

- **`HttpPipelineClient.SendForStringAsync` is chained with `ThenAsync`.** Sending and reading the body are
  two steps that can each fail; the async combinator short-circuits the second when the first fails, which
  removes the hand-written check-and-forward in between. `SendForStringAsync` also carries the status code and
  `Retry-After` into the error before the response is disposed — the caller only ever receives a `Result`, and
  by then there is no second chance to read those headers.
  `SendForStringAsync` 改用非同步組合子串接,失敗自動短路;錯誤也帶上狀態碼與 `Retry-After`。

### Notes

- Targets `net10.0`. Dependencies unchanged apart from `Ozakboy.Core.Abstractions` 0.2.1 → 0.3.0. No
  third-party package appears anywhere in the transitive graph.
  相依不變,僅 `Ozakboy.Core.Abstractions` 升到 0.3.0;遞移相依樹中仍無任何第三方套件。
- `Precision.TryParsePlain` has no direct use here: this package serialises decimals (through
  `Precision.ToPlainString`, in `QueryParameters`) but never parses them. It is used indirectly, since
  `Error.TryGetDecimal` reads through it.
  `Precision.TryParsePlain` 無直接適用處(本套件只序列化、不解析小數);經由 `Error.TryGetDecimal` 間接用到。
- 188 tests, all green; line coverage 96.1% on `Ozakboy.Http`.
  188 個測試全綠,行涵蓋率 96.1%。

## [0.1.0] - 2026-09-11

First release. A signing, rate-limiting, retry and log-masking pipeline for `HttpClient`, assembled from the
official `HttpClientFactory` and `DelegatingHandler` primitives so that nothing third-party enters the
dependency graph. Written for an automated trading engine, but nothing about trading leaked into the code.

首次發行。以官方 `HttpClientFactory` 與 `DelegatingHandler` 組成的簽章、限流、重試與脫敏日誌管線,
相依樹裡不引入任何第三方套件。為自動交易引擎而寫,但程式碼裡沒有任何交易所專屬邏輯。

### Added

- **Signing handler.** `SigningHandler` attaches an HMAC signature and the API-key header to requests marked
  with `WithSignature()`. The algorithm is substitutable through `ISignatureAlgorithm`, with
  `HmacSha256SignatureAlgorithm` as the default (lowercase hex, via `Convert.ToHexStringLower`).
  簽章處理器。演算法可抽換,預設為 HMAC-SHA256。

- **`QueryParameters` — an ordered, immutable parameter container.** Signing parameters deliberately cannot be
  carried in a `Dictionary`: its enumeration order is unspecified, and an HMAC signs the concatenated string,
  so a shifted order produces intermittent signature errors that disappear on retry. Encoding uses
  `Uri.EscapeDataString` (a space becomes `%20`, never `+`) and decimals go through `Precision.ToPlainString`,
  so the string signed and the string sent match byte for byte.
  有序且不可變的參數容器,把「字典順序無保證」這條路封死;先編碼再簽,兩邊字串逐字相同。

- **Weighted multi-bucket rate limiting.** `WeightedRateLimiter` expresses several concurrent quota windows and
  a per-request weight. The quota arithmetic is `System.Threading.RateLimiting`'s `TokenBucketRateLimiter`;
  replenishment timing is driven by `TimeProvider` instead of the primitive's real-clock gate, which is what
  makes the multi-bucket behaviour testable under a fake clock.
  多桶加權限流。配額計算沿用官方原語,補充時機改由 `TimeProvider` 驅動,假時鐘才測得出來。

- **Idempotency-aware retry.** `RetryHandler` retries safe methods only; `POST`, `DELETE`, `PUT` and `PATCH`
  are not retried unless the caller declares `AsIdempotent()`. A timeout does not mean the peer never received
  the request, and in a trading system resending an order means placing it twice. `Retry-After` overrides the
  computed backoff, capped by `MaxRetryAfter`.
  冪等感知的重試。非冪等請求絕不重試,並尊重 `Retry-After`。

- **Sanitising logging.** `SanitizingLoggingHandler` logs through the `ILogger` abstraction and masks sensitive
  query values while keeping the path and harmless parameters visible. Masking is fail-closed: a value that
  cannot be masked is discarded, never emitted raw. Masking reuses `Ozakboy.Security`'s `SecretMasker` rather
  than growing a second vocabulary of sensitive names.
  脫敏日誌。遮罩失敗一律捨棄,絕不 fail-open。

- **Error model and two-layer timeouts.** `HttpErrorMapper` maps status codes and transport exceptions onto
  `Error` with a category that already answers "is retrying worthwhile": 429, 408 and every 5xx are transient,
  other 4xx codes are not, and a caller cancellation is neither transient nor alert-worthy.
  `HttpTimeoutOptions` separates the per-attempt bound from the overall bound.
  錯誤模型與兩層逾時(單次嘗試、整趟請求)。

- **`HttpPipelineClient`.** Folds the exception path back into `Result<T>`, so callers handle one shape rather
  than remembering which exceptions to catch.
  門面型別,把例外收斂回 `Result<T>`。

- **DI registration.** `AddOzakboyHttpPipeline` attaches all four handlers in the one order that works, and
  registers the rate limiter as a singleton per client name — `IHttpClientFactory` rebuilds handler chains
  periodically, and a per-handler limiter would reset its quota on every rebuild.
  相依性注入註冊,順序固定,限流器為每個用戶端名稱的單例。

### Notes

- Targets `net10.0`.
- Dependencies: `Microsoft.Extensions.Http`, `Microsoft.Extensions.Logging.Abstractions`,
  `Microsoft.Extensions.Options`, `System.Threading.RateLimiting`, `Ozakboy.Core.Abstractions`,
  `Ozakboy.Security`. No third-party package appears anywhere in the transitive graph.
- `Microsoft.Extensions.Http.Resilience` is deliberately excluded: it carries a Microsoft name but depends on
  the third-party `Polly.Core`.
  刻意排除 `Microsoft.Extensions.Http.Resilience`(遞移相依第三方的 `Polly.Core`)。

[Unreleased]: https://github.com/ozakboy/Ozakboy.Http/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/ozakboy/Ozakboy.Http/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/ozakboy/Ozakboy.Http/releases/tag/v0.1.0

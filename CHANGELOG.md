# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/ozakboy/Ozakboy.Http/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ozakboy/Ozakboy.Http/releases/tag/v0.1.0

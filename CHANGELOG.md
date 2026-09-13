# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.3.1] - 2026-09-13

The facade may be a singleton; the `HttpClient` inside it may not. `CreateOzakboyHttpPipelineClient` now hands the
facade the factory instead of a client built from it, so `IHttpClientFactory` gets to rotate handlers and DNS keeps
up when the peer moves to a new IP.

門面可以是單例,它裡面的 `HttpClient` 不行。`CreateOzakboyHttpPipelineClient` 改為把工廠本身交給門面,
而不是工廠建出來的用戶端,`IHttpClientFactory` 的處理器輪替才有機會發生,對方換 IP 之後 DNS 才跟得上。

### Fixed

- **A facade built by `CreateOzakboyHttpPipelineClient` held one `HttpClient` for its whole life, so handlers
  never rotated.** The facade is meant to be a singleton — `services.AddSingleton(p =>
  p.CreateOzakboyHttpPipelineClient("exchange"))` is the documented registration, and that is how
  `Ozakboy.TradeKit.Binance` registers it — but it took an `HttpClient` at construction and kept it. Handler
  rotation under `SetHandlerLifetime` (two minutes by default) only gets its chance on *each* `CreateClient`
  call, so one long-lived client stays pinned to a single handler and the connections it has already opened, and
  the process never picks up a new address for a host that has moved. Exchanges do move, and nothing in the logs
  says so: on a 24-hour unattended run the symptom is simply that requests stop getting through. The facade now
  holds the `IHttpClientFactory` and the client name and calls `CreateClient` per request, which is the official
  guidance — `CreateClient` is cheap and the expensive part, the handler, is pooled by the factory. The infinite
  `HttpClient.Timeout` set in `AddOzakboyHttpPipeline`'s `ConfigureHttpClient` comes along with every client built
  that way, as before.
  `CreateOzakboyHttpPipelineClient` 建出的門面長期持有同一個 `HttpClient`,處理器永遠不會輪替:門面本來就是要註冊成單例的
  (下游 `Ozakboy.TradeKit.Binance` 正是如此),但它在建構時取一個用戶端就一直用下去。`SetHandlerLifetime`(預設兩分鐘)
  的輪替只在**每一次** `CreateClient` 時才有機會發生,長期持有等於永遠綁在同一個處理器與它已開的連線上,對方換 IP 之後就再也連不上,
  而日誌上看不出任何原因 —— 無人值守跑一整天,症狀只是「請求送不出去了」。現在門面持有的是 `IHttpClientFactory` 與用戶端名稱,
  每次請求各呼叫一次 `CreateClient`(這是官方建議用法:`CreateClient` 很便宜,昂貴的處理器由工廠池化與輪替)。
  `AddOzakboyHttpPipeline` 在 `ConfigureHttpClient` 設定的無限 `HttpClient.Timeout` 一如以往,每次建出來都會帶到。

### Added

- **`HttpPipelineClient(IHttpClientFactory, clientName, timeouts, timeProvider, masker)`.** The constructor the
  DI path now uses; the client name is the one given to `AddHttpClient`. Every 0.3.0 signature is still there and
  none was changed.
  新增接受 `IHttpClientFactory` 與用戶端名稱的建構式(DI 路徑改用它);0.3.0 的所有簽章都保留,沒有任何一個被改動。

### Notes

- **Both construction paths remain, and they differ in who owns the client's lifetime.** Passing an `HttpClient`
  still behaves exactly as before — the facade uses that one client and never obtains another — and the caller
  owns its lifetime and therefore its DNS behaviour. That path is for code that assembles the pipeline by hand
  (`Ozakboy.Telegram` uses it). Code registering through a service container should build the facade with
  `CreateOzakboyHttpPipelineClient`, which takes the factory path.
  兩條建構路徑都在,差別在於誰負責用戶端的生命週期:傳入 `HttpClient` 的那條完全照舊(門面就用那一個,不會另外取),
  生命週期與 DNS 行為由呼叫端負責,適用於手動組裝管線的程式(`Ozakboy.Telegram` 用它);
  走服務容器的請以 `CreateOzakboyHttpPipelineClient` 建立門面,那條走工廠路徑。
- The masker travels with the facade, not with the `HttpClient`, so error masking is unaffected; the secret-leak
  suite is unchanged and still green.
  遮罩器跟著門面走、不是跟著 `HttpClient`,錯誤遮罩完全沒有受到影響;祕密外洩測試未修改,仍然全綠。
- 221 tests, all green (215 from 0.3.0 plus 6 new). Taking the client at construction again turns the two
  "a client per request" tests red: `CreateClient` is called once instead of three times.
  221 個測試全綠(0.3.0 的 215 條加 6 條新的)。把用戶端改回建構時取一次,「每次請求各取一個」的兩條測試會變紅 ——
  `CreateClient` 只被呼叫一次,而不是三次。
- Targets `net10.0`. Dependencies unchanged; no third-party package anywhere in the transitive graph.
  相依不變,遞移相依樹中仍無任何第三方套件。

## [0.3.0] - 2026-09-12

Fixes the pipeline order — which in 0.2.0 was the reverse of what its own comments described — and closes the
places where a secret could leave the package unmasked. Two rules sit behind the new order: every attempt is a
new request, so every attempt pays its own rate-limit weight and is signed afresh; and a timestamp is the
moment the request goes out, so signing happens only after the rate-limit permit is held.

修正管線順序(0.2.0 的實際順序與它自己的註解相反),並補上祕密可能未經遮罩就流出本套件的缺口。
新順序背後有兩條原則:每一次嘗試都是一個新請求 —— 各自付權重、各自重新簽章;時間戳要是送出那一刻的 —— 拿到限流許可之後才簽章。

### Fixed

- **The pipeline order contradicted its own comments.** 0.2.0 attached signing → rate limiting → retry →
  logging. `IHttpClientFactory` puts the first-registered handler outermost, so retry sat *inside* signing and
  rate limiting, while the comments claimed rate limiting was "outside retry, so each retry pays its own
  weight". The reasoning was exactly backwards. The order is now **retry (outermost) → rate limiting → signing
  → logging (innermost)**, with the reason for each position written into the XML docs, and tests that go red
  under the 0.2.0 order. A development draft of 0.3.0 used retry → signing → rate limiting instead; that signed
  requests before they queued for permits, and the limiter may wait 30 seconds against Binance's 5-second
  recvWindow, so a request that queued long enough was rejected on arrival (`-1021`). The final order signs only
  once the permit is held, pinned by a test that goes red under the draft order.
  管線順序與註解相反:0.2.0 依「簽章 → 限流 → 重試 → 日誌」掛上,重試實際在簽章與限流之內,註解卻宣稱限流在重試外層。
  新順序為「重試(最外層)→ 限流 → 簽章 → 日誌(最內層)」。0.3.0 開發中曾用「重試 → 簽章 → 限流」,
  會先簽章再排隊,排隊超過 recvWindow 就被拒絕(`-1021`);定案版拿到許可後才簽章,兩種錯誤順序都有會變紅的測試鎖住。

- **Retries paid no rate-limit weight.** With the limiter outside retry, it was traversed once per request, so a
  request retried N times paid for one. The local quota under-counted real usage exactly when the error rate was
  high — which, for a service that bans by weight (Binance's 418), is the worst moment to be wrong. Every attempt
  now pays its own weight, and a retry that finds the quota exhausted waits for permits rather than bypassing
  the limiter. The backoff wait itself holds no permit.
  重試不付權重:限流器在重試外層只被穿過一次,重試 N 次只付一份,錯誤率高時本地配額低估實際用量。
  現在每次嘗試各付權重,配額不足時重試會等待許可而不是繞過;退避等待期間不持有任何許可。

- **Retries reused the old timestamp.** With signing outside retry, every attempt carried the first attempt's
  signature and timestamp, so after a long backoff the retry was rejected by the peer's time window (Binance
  `-1021`). Every attempt is now re-signed, after its permit is granted; set the new
  `SigningOptions.TimestampParameterName` and the signing handler restamps that parameter with the current time
  on each attempt (in place, so the signing order is unchanged).
  重試沿用舊時間戳:現在每次嘗試都在拿到許可後重新簽章;設定新的 `SigningOptions.TimestampParameterName`,時間戳參數會在原位換成當下時間。

- **Exceptions reached the logger and `Error` unmasked.** `SanitizingLoggingHandler` handed the original
  exception object to the logger, and `HttpErrorMapper.FromException` spliced the exception message into
  `Error.Message` and kept the original in `Error.Exception`. A transport exception's message often carries the
  request URI, and some services put the credential in its path; registered secrets had no effect on either
  route. Both now use `SanitizedException` (see Added), and `Error.Message` and `Error.Data` go through literal
  replacement of registered secrets.
  例外未經遮罩:日誌處理器把原始例外交給記錄器,`HttpErrorMapper.FromException` 把例外訊息接進 `Error.Message`、
  原物件放進 `Error.Exception`,已登記的祕密對這兩條路徑都無效。現在一律改用 `SanitizedException`。

- **`IHttpClientFactory`'s default logging leaked full URIs.** Its `LogicalHandler` / `ClientHandler` logging
  writes the full URI at Information level, and .NET redacts only the query, not the path — a straight leak for
  a service with the credential in the path, such as Telegram's `/bot<token>/`. `AddOzakboyHttpPipeline` now
  calls `RemoveAllLoggers()` on the client it builds; the package's own logging handler is masked.
  預設 factory 日誌外洩完整位址(.NET 只遮 query、不遮路徑):`AddOzakboyHttpPipeline` 現在會呼叫 `RemoveAllLoggers()`。

- **A timeout could be misfiled as a caller cancellation.** `HttpClient.Timeout` defaults to 100 seconds; when
  that was shorter than `HttpTimeoutOptions.OverallTimeout` it fired first, surfaced as cancellation, and was
  neither retried nor alerted on. `AddOzakboyHttpPipeline` now sets it to `Timeout.InfiniteTimeSpan` and leaves
  timing to the pipeline.
  `HttpClient.Timeout` 預設 100 秒若短於整體逾時,逾時會被錯歸為「取消」;現在設為無限,逾時交由管線處理。

- **An overall timeout during a rate-limit wait was reported as a cancellation.** When
  `HttpPipelineClient`'s overall timeout fired while the request was still queueing for permits, the limiter
  reported `http.cancelled` and the facade passed it on, although the caller cancelled nothing. It is now
  reported as `http.timeout`.
  整體逾時在排隊等許可時觸發,原本被回報為「取消」;現在回報為 `http.timeout`。

### Added

- **Secret registration.** `AddOzakboyHttpPipeline` registers `Signing.ApiKey`, `Signing.SecretKey`, and every
  value in the new `HttpPipelineOptions.KnownSecrets` on a per-client masker — a singleton keyed by client name,
  so secrets survive `IHttpClientFactory` rebuilding its handler chain. `IServiceProvider.GetOzakboyHttpMasker(clientName)`
  returns that masker, for secrets that only turn up at run time (a listenKey in a WebSocket path, say) and for
  handing to `HttpPipelineClient`. Known secrets shorter than 8 characters are rejected at registration, with a
  message that never echoes the value; signing keys that short are skipped.
  祕密登記入口:簽章金鑰與新的 `HttpPipelineOptions.KnownSecrets` 自動登記到每個用戶端一份的遮罩器(以名稱為鍵的單例);
  執行期取得的祕密以 `GetOzakboyHttpMasker(clientName)` 取得同一個遮罩器再登記。

- **`IServiceProvider.CreateOzakboyHttpPipelineClient(clientName)`, the recommended way to build the facade.**
  It creates an `HttpPipelineClient` for a client registered with `AddOzakboyHttpPipeline`, bringing in that
  client's masker and the very timeouts the pipeline's retry handler uses. Built by hand, forgetting the masker
  raises no error — the facade just quietly knows only `SecretMasker.Default` — and hand-passed timeouts can drift
  from the pipeline's. It is a provider method rather than another registration so the lifetime stays the
  caller's: `services.AddSingleton(p => p.CreateOzakboyHttpPipelineClient("exchange"))`.
  建立門面的建議做法:自動帶入該用戶端的遮罩器與管線同一份逾時設定,避免漏傳遮罩器;做成服務容器上的方法,生命週期由呼叫端決定。

- **`SanitizedException`.** The stand-in used wherever an exception leaves the package: it keeps the original
  type name (`OriginalExceptionType`), the masked message and the masked full `ToString()` including the stack
  (`SanitizedDetails`), but has no inner exception and holds no reference to the original object.
  取代原始例外的替身:保留型別名稱、遮罩後的訊息與遮罩後的堆疊,但沒有內層例外、不參照原物件。

- **`SigningOptions.TimestampParameterName`.** Defaults to `null`, which leaves every parameter alone.
  每次簽章都重新蓋時間戳的參數名,預設 `null`(不動任何參數)。

- **Overloads taking a masker or a time source.** `HttpPipelineClient(httpClient, timeouts, timeProvider, masker)`,
  `RetryHandler(options, timeouts, timeProvider, masker)`, `SanitizingLoggingHandler(logger, options,
  timeProvider, masker)`, `HttpErrorMapper.FromException(exception, masker)`, and `SigningHandler(options,
  timeProvider)`. Every 0.2.0 signature is still there, so code compiled against 0.2.0 keeps binding.
  新增接受遮罩器或時間來源的多載;0.2.0 的所有簽章都保留。

### Changed

What callers will notice:
呼叫端會注意到的行為變更:

- **Every retry is signed afresh and pays its own weight.** Under failures the local limiter now fills faster
  than before — which is the point, since it now matches what the peer counts — and a retry may wait for
  permits. Signing happens after the permit, so a timestamp never ages in the queue.
  每次重試都重新付權重並重新簽章:錯誤時本地配額消耗得比以前快(這才對得上對方的計算),重試可能要等許可;
  簽章在拿到許可之後,時間戳不會在排隊時過期。

- **The attempt timeout no longer counts time spent queueing for permits.** It starts once the permit is held and
  the request goes out: it measures how long the peer takes to answer. Counted from the start of the attempt,
  Binance's default 10-second attempt bound against the limiter's 30-second ceiling would time out any request
  that queued past 10 seconds and retry it at the back of the queue. The overall timeout still covers the queue.
  單次嘗試逾時不再計入排隊等許可的時間,從拿到許可、開始送出時起算;整體逾時仍涵蓋排隊。

- **A local rate-limit timeout (`http.rate_limit.timeout`) is not retried by the default policy.** The request
  never went out and has already waited out the limiter's ceiling, so a retry only multiplies the wait by the
  attempt count. It comes back as `RateLimited` (not `Exhausted`). A policy with a `RetryPredicate` decides for
  itself, in keeping with the predicate replacing the built-in check.
  本地限流逾時預設策略不重試(回報為 `RateLimited`、不是 `Exhausted`);策略設了 `RetryPredicate` 時由它決定。

- **`Error.Exception` and `ResultException.InnerException` are `SanitizedException`,** never the original type.
  Code that did `ex.InnerException is HttpRequestException` should branch on `Error.Code` / `Error.Category`
  instead; the original type name is in `SanitizedException.OriginalExceptionType`.
  `Error.Exception` 與 `ResultException.InnerException` 一律是 `SanitizedException`,請改以 `Error.Code` / `Error.Category` 分支。

- **`HttpPipelineClient` masks every failure it returns** — code, message, each data entry — with the masker it
  was given. Build it with `provider.CreateOzakboyHttpPipelineClient(name)`, which passes the client's; a
  hand-built one without a masker uses `SecretMasker.Default` and knows only the secrets registered there.
  `HttpPipelineClient` 回傳的每個失敗都經遮罩;請以 `CreateOzakboyHttpPipelineClient` 建立,否則只認得 `SecretMasker.Default` 上的祕密。

- **Clients built with `AddOzakboyHttpPipeline` have no default factory logging, and an infinite
  `HttpClient.Timeout`,** overriding any `Timeout` configured earlier. Bound the whole exchange through
  `HttpTimeoutOptions.OverallTimeout` (via `HttpPipelineClient`) or your own cancellation token.
  以 `AddOzakboyHttpPipeline` 建立的用戶端不再有預設 factory 日誌,`HttpClient.Timeout` 為無限(會覆蓋先前的設定)。

- **`AddSanitizedLogging` registers a per-client masker too,** so a secret registered at run time survives handler
  rebuilds; `AddRetry` uses that masker when one is registered for the same client name.
  `AddSanitizedLogging` 也改為每個用戶端一份的遮罩器,執行期登記的祕密不會隨處理器重建而消失。

### Notes

- Targets `net10.0`. Dependencies unchanged; no third-party package anywhere in the transitive graph.
  相依不變,遞移相依樹中仍無任何第三方套件。
- The test helper that drives `FakeTimeProvider` now separates steps with a short real-time beat and is bounded
  by real time rather than a step count. The old version could burn through its 200 steps during a cold start or
  a coverage run before the work had even registered its timer, then hang.
  測試用的假時鐘推進器改以真實時間節拍推進、以真實時間為上限;舊版在冷啟動或涵蓋率回合下會在工作掛上計時器前就用完步數。
- 215 tests, all green across two normal runs and one coverage run; line coverage 95.9% on `Ozakboy.Http`.
  Moving signing back ahead of rate limiting turns the timestamp-after-release test red.
  215 個測試,兩次一般回合與一次涵蓋率回合全綠;行涵蓋率 95.9%。把簽章移回限流之前,時間戳必須晚於放行時刻的測試會變紅。

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

[Unreleased]: https://github.com/ozakboy/Ozakboy.Http/compare/v0.3.1...HEAD
[0.3.1]: https://github.com/ozakboy/Ozakboy.Http/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/ozakboy/Ozakboy.Http/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/ozakboy/Ozakboy.Http/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/ozakboy/Ozakboy.Http/releases/tag/v0.1.0

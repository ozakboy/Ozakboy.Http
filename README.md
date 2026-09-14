# Ozakboy.Http

A signing, rate-limiting, retry and log-masking pipeline for `HttpClient`, built entirely on first-party Microsoft packages.

English | [繁體中文](README_zh-TW.md)

```
dotnet add package Ozakboy.Http
```

Requires .NET 10. Depends only on Microsoft packages and `Ozakboy.*`.

---

## Why this exists

An automated trading engine needs four things from its HTTP layer: every private request signed, the exchange's weight quota respected, transient faults retried without ever resending an order, and a log that never contains an API key. The usual answers are RestSharp, Flurl and Polly.

This package does those four things on top of the official `HttpClientFactory` and `DelegatingHandler` pipeline, with no third-party dependency in the graph.

> A note on `Microsoft.Extensions.Http.Resilience`: it carries a Microsoft name but depends on the third-party `Polly.Core`. Judged by the transitive graph rather than by package name, it does not qualify — which is why the retry handler here is written from scratch.

---

## The pipeline

Four handlers, and **the order matters**:

```
retry → rate limiting → signing → sanitising logging → the network
```

Two rules sit behind it: **every attempt is a new request**, and **a timestamp is the moment the request goes out**. Retry sits outermost, so the other three run again on every attempt:

- **Rate limiting inside retry.** Every attempt pays its own weight. Outside retry, a request retried N times would pay once, and the local quota would under-count exactly when the error rate is high — which is when a weight-based ban (418) arrives. The backoff wait happens outside the limiter and holds no permit.
- **Signing inside rate limiting.** A request is signed, and with `Signing.TimestampParameterName` set, stamped with the current time, only once its permit is held. Sign first and queue afterwards, and the timestamp ages in the queue: the limiter waits up to 30 seconds by default, while Binance's recvWindow is 5. Every retry is re-signed the same way. The signing handler is also what writes `WithQueryParameters` into the URI; with `EnableSigning = false` an internal handler takes the same position and writes the parameters with the same order, encoding and replace-the-existing-query rule, only unsigned (before 0.3.3 an unsigned pipeline dropped them).
- **Logging innermost.** It records what actually went out: admitted, signed, and which attempt it was.

> **This order took two fixes.** 0.2.0 attached signing → rate limiting → retry → logging while its comments described the opposite, so retries reused a stale timestamp and paid no weight. A 0.3.0 draft then tried retry → signing → rate limiting, which signed requests before they queued, so one that queued past recvWindow was rejected on arrival (`-1021`). A wrong order raises no error, only hard-to-read runtime behaviour, so every point is pinned by a test. See the [changelog](CHANGELOG.md).

```csharp
services.AddSingleton(TimeProvider.System);

services.AddHttpClient("exchange", client => client.BaseAddress = new Uri("https://api.example.com"))
    .AddOzakboyHttpPipeline(options =>
    {
        options.Signing.ApiKey = configuration["Exchange:ApiKey"]!;
        options.Signing.SecretKey = configuration["Exchange:SecretKey"]!;
        options.Signing.ApiKeyHeaderName = "X-MBX-APIKEY";
        options.Signing.TimestampParameterName = "timestamp";   // restamped on every attempt

        options.RateLimiting.Buckets.Add(new RateLimitBucket("minute", 2400, TimeSpan.FromMinutes(1)));
        options.RateLimiting.Buckets.Add(new RateLimitBucket("second", 300, TimeSpan.FromSeconds(1)));

        options.Retry.Policy = RetryPolicy.Default;
        options.Timeouts.AttemptTimeout = TimeSpan.FromSeconds(10);
        options.Timeouts.OverallTimeout = TimeSpan.FromSeconds(30);
    });

// Recommended: the facade picks up this client's masker and the same timeouts from the registration.
services.AddSingleton(provider => provider.CreateOzakboyHttpPipelineClient("exchange"));
```

`CreateOzakboyHttpPipelineClient` is the recommended way to get an `HttpPipelineClient`. The facade is the last checkpoint an error passes before leaving the package, and it masks with whatever masker it was built with; constructed by hand, forgetting the masker raises no error, it just leaves that checkpoint knowing only `SecretMasker.Default`. The overall timeout comes from the same registration, so it cannot drift from the one the retry handler uses. It is a method on the service provider rather than another registration so the lifetime stays yours.

**Why the facade can be a singleton while an `HttpClient` must not be held.** The `AddSingleton` above is deliberate: the facade carries no mutable state — timeouts, time source and masker are read-only settings. What it does *not* do is hold an `HttpClient`. It holds the `IHttpClientFactory` and the client name, and takes a client per request. That distinction is the whole point: `IHttpClientFactory` rotates its handlers on a lifetime (`SetHandlerLifetime`, two minutes by default), and rotation only gets its chance on each `CreateClient` call. A client captured once and kept stays bound to a single handler and the connections it has already opened, so the process never learns that the host has moved to a new address — and exchanges do move. Nothing in the logs explains it; on a long unattended run the requests simply stop getting through. `CreateClient` is cheap and the expensive part — the handler — is pooled by the factory, so calling it per request is the official guidance rather than a workaround.

### What `AddOzakboyHttpPipeline` does to the client

Besides attaching the four handlers, it changes three things on the named client:

- **It registers secrets.** `Signing.ApiKey`, `Signing.SecretKey`, and every value in `options.KnownSecrets` go onto a masker that belongs to this client — a singleton keyed by client name, so it survives `IHttpClientFactory` rebuilding its handlers. A secret that only turns up at run time goes onto the same masker with `provider.GetOzakboyHttpMasker("exchange").RegisterKnownSecret(value)`.
- **It removes `IHttpClientFactory`'s default logging** (`RemoveAllLoggers()`). That logging writes the full URI at Information level, and .NET redacts the query, not the path — a credential in the path, like Telegram's `/bot<token>/`, goes straight into the log. The pipeline's own logging handler is masked and replaces it.
- **It sets `HttpClient.Timeout` to infinite.** Timeouts belong to the pipeline: the retry handler bounds each attempt, `HttpPipelineClient` the whole exchange. The default 100 seconds, if shorter than `OverallTimeout`, would fire first as a cancellation, and a timeout would be misfiled as the caller cancelling. Any `Timeout` configured earlier is overridden.

Each section can also be registered on its own — `AddRetry`, `AddWeightedRateLimiting`, `AddRequestSigning`, `AddSanitizedLogging` — in that order. Getting the order right is then your job. `AddRequestSigning` is also what writes `WithQueryParameters` into the URI, so a segmented pipeline without it sends no query parameters.

---

## Signing: three details that cause intermittent failures

**Parameter order changes the signature.** An HMAC signs one concatenated string, so a different order is a different signature. Signing parameters therefore never live in a `Dictionary` — its enumeration order is unspecified, and when it shifts you get intermittent signature errors that vanish on retry. `QueryParameters` is append-only, and that order is the signing order.

```csharp
var parameters = QueryParameters.CreateBuilder()
    .Add("symbol", "BTCUSDT")
    .Add("quantity", 0.001m)        // "0.001", never "1E-03"
    .Add("timestamp", timestamp)
    .Build();

using var request = new HttpRequestMessage(HttpMethod.Post, "/fapi/v1/order")
    .WithQueryParameters(parameters)
    .WithSignature()
    .WithWeight(1);
```

**Encode first, then sign.** The string signed must match the string sent, byte for byte. Encoding uses `Uri.EscapeDataString`, never `HttpUtility.UrlEncode` — the latter encodes a space as `+` rather than `%20`, and the two sides then disagree.

**Decimals get no exponent and no trailing zeros.** Serialisation goes through `Precision.ToPlainString`. A quantity sent as `1E-05` usually comes back as an opaque parameter error that gives no hint of the real cause.

The algorithm is substitutable through `ISignatureAlgorithm`; `HmacSha256SignatureAlgorithm` is the default and emits lowercase hex.

---

## Rate limiting: weighted, multi-bucket, fake-clock testable

Exchanges enforce several windows at once and charge different weights per endpoint. `RateLimitBucket` describes one window; a request is admitted only when every bucket can cover its weight.

The quota arithmetic is `System.Threading.RateLimiting`'s `TokenBucketRateLimiter` — no rate-limiting algorithm is implemented here. What *is* implemented is the clock: the primitive's replenishment is tied to the real clock even with auto-replenishment off, which makes multi-bucket behaviour untestable. `WeightedRateLimiter` drives replenishment from a `TimeProvider` instead, so tests advance a `FakeTimeProvider` rather than sleeping.

A request whose weight exceeds the smallest bucket fails immediately rather than waiting forever, and a wait longer than `AcquisitionTimeout` fails as `ErrorCategory.RateLimited` without the request ever leaving the machine.

---

## Retry: non-idempotent requests are never retried

This is the part that matters most. A timeout says no response arrived, **not** that the peer never received the request — the connection can drop on the way back, long after the order was accepted. Resending means placing it twice, and the position drift usually surfaces only at reconciliation.

- Safe methods (`GET`, `HEAD`, `OPTIONS`, `TRACE`) are retried.
- `POST`, `DELETE`, `PUT`, `PATCH` are **not**, by default.
- `request.AsIdempotent()` opts a request in; `request.AsNonIdempotent()` opts one out.

Making it an explicit declaration is deliberate. With a boolean, the default `false` looks identical to a considered decision not to retry, and a code review cannot tell them apart.

Backoff comes from `RetryPolicy.GetDelay`. When a response carries `Retry-After`, the peer's instruction wins instead (capped by `MaxRetryAfter`) — the server knows how much of its cooldown remains, and coming back early only extends the ban.

Whether a failure is worth retrying is decided entirely by the policy. Setting `RetryPolicy.RetryPredicate` replaces the built-in transient check outright — not as an extra condition — and the error it is handed already carries what such a decision needs:

```csharp
var policy = RetryPolicy.Default with
{
    // A 429 that says when to come back is worth another go; one that does not usually means the address is banned.
    RetryPredicate = error => error.TryGetDecimal(HttpErrorDataKeys.RetryAfterSeconds, out _),
};
```

When the attempts run out on a failure that *was* worth retrying, the error handed back is re-labelled `ErrorCategory.Exhausted`: the code and message are kept, but `IsTransient` turns false, so the caller's own retry layer does not multiply the same fault by another round.

Timeouts come in two layers: `AttemptTimeout` bounds one attempt, `OverallTimeout` bounds the whole exchange including backoff waits. The attempt clock starts only once the rate-limit permit is held: it measures how long the peer takes to answer, not how long the request queued. Otherwise, with Binance's default 10-second attempt bound shorter than the limiter's 30-second ceiling, any request that queued past 10 seconds would time out and be retried at the back of the queue. The overall bound still covers the queue.

A local rate-limit timeout (`http.rate_limit.timeout`) is not retried by the default policy: the request never went out and has already waited out the limiter's full ceiling, so another attempt only multiplies the wait. A policy with a `RetryPredicate` decides for itself.

---

## Logging: masked, and fail-closed

`SanitizingLoggingHandler` writes through the `ILogger` abstraction and binds to no concrete logging implementation. Sensitive query values are masked while the path and the harmless parameters survive, because debugging needs to show which endpoint was called:

```
HTTP 送出 GET https://api.example.com/fapi/v1/order?symbol=BTCUSDT&apiKey=vmPU****Eh8A&signature=****
```

Masking is `Ozakboy.Security`'s `SecretMasker`, whose default name list already covers `apiKey`, `signature`, `token`, `authorization` and friends, matched case-insensitively. Add your own through `AdditionalSensitiveParameterNames`, and register the key value itself with `RegisterKnownSecret` to catch it wherever a peer echoes it back.

**If masking fails, the value is discarded — never emitted raw.** Fail-open here would write the credential straight into the log while everything still looked fine.

**Exceptions never reach a logger, or an `Error`, as themselves.** A transport exception's message often carries the request URI, and an exception object cannot be masked: `Message` is read-only and the inner chain cannot be rewritten. So the logger receives a `SanitizedException` instead — the original type name, the masked message, and the masked `ToString()` with its stack — and every `Error.Exception` this package produces is one too, with `Error.Message` and `Error.Data` passed through the same replacement. Name rules cannot reach a path segment or an exception message; only registered values can, which is why the pipeline registers the signing keys for you and `KnownSecrets` exists.

---

## Failures come back as `Result<T>`

`HttpPipelineClient` sits in front of the assembled pipeline and converts the exception path back into `Result<T>`, so callers handle one shape rather than remembering which exceptions to catch.

```csharp
// From a service container: the facade takes a client from the factory per request.
var client = provider.CreateOzakboyHttpPipelineClient("exchange");

var result = await client.SendForStringAsync(request, cancellationToken);
if (!result.TryGetValue(out var body))
{
    // Category already says whether retrying is worthwhile
    if (result.Error.IsTransient) { /* back off and come round again */ }
    return result.ToFailure<Order>();
}
```

`HttpErrorMapper` does the classification: 429 is `RateLimited`, 408 is `Timeout`, every 5xx is `Unavailable` — all transient. Other 4xx codes are not: the request itself is wrong, and resending it unchanged earns the same answer plus another slice of quota. A caller cancellation maps to `Cancelled`, which is not transient — it deserves neither a retry nor an alert.

Diagnostic values travel in `Error.Data` under the keys in `HttpErrorDataKeys`, and they read back typed: `error.TryGetInt64(HttpErrorDataKeys.StatusCode, out var status)` and `error.TryGetDecimal(HttpErrorDataKeys.RetryAfterSeconds, out var seconds)`. Nothing has to be parsed back out of a string at the far end, and no consumer gets the chance to forget `InvariantCulture`.

Callers who use a bare `HttpClient` instead of the facade catch `ResultException` from `Ozakboy.Core.Abstractions`. It is the one carrier every Ozakboy package uses to move an `Error` across a boundary whose signature belongs to the BCL, its `Error` property is never null, and it derives from `InvalidOperationException` — so there is a single exception type to catch rather than one per package.

Its `InnerException`, like `Error.Exception`, is a `SanitizedException`, never the original type: branch on `Error.Code` and `Error.Category` rather than on exception types. And build `HttpPipelineClient` with `CreateOzakboyHttpPipelineClient`, which hands it the client's masker; it is the last checkpoint an error passes on its way out, and without that masker it only knows the secrets on `SecretMasker.Default`.

There is a second construction path for pipelines assembled by hand, outside a service container: `new HttpPipelineClient(httpClient, timeouts, timeProvider, masker)`. The facade then uses the client it was handed and never obtains another, so that client's lifetime — and with it whether handlers ever rotate and DNS stays current — is the caller's responsibility. Within a service container, prefer `CreateOzakboyHttpPipelineClient`, which takes the factory path described above.

---

## Testing

`dotnet test` runs 215 tests with no network and no `Thread.Sleep`. The pipeline order is pinned by tests that go red under the 0.2.0 order and under the signing-before-rate-limiting draft, and a canary secret is walked through the failure paths to check every log form and every field of the returned error. Signing is pinned to golden vectors published in the Binance documentation, with counter-proofs that parameter order and encoding order really do change the result. Rate limiting and retry timing run on `FakeTimeProvider`.

---

## Licence

MIT. See [LICENSE](LICENSE).

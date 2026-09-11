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
signing → rate limiting → retry → sanitising logging → the network
```

Getting it wrong produces no error at all, only behaviour that is hard to read at runtime:

- **Signing inside retry** re-signs on every attempt with a fresh timestamp. Some services reject that outright.
- **Rate limiting inside retry** means a retry storm consumes no weight, and blows through the peer's quota.
- **Logging anywhere but innermost** records what the caller intended to send rather than what actually went out.

```csharp
services.AddSingleton(TimeProvider.System);

services.AddHttpClient("exchange", client => client.BaseAddress = new Uri("https://api.example.com"))
    .AddOzakboyHttpPipeline(options =>
    {
        options.Signing.ApiKey = configuration["Exchange:ApiKey"]!;
        options.Signing.SecretKey = configuration["Exchange:SecretKey"]!;
        options.Signing.ApiKeyHeaderName = "X-MBX-APIKEY";

        options.RateLimiting.Buckets.Add(new RateLimitBucket("minute", 2400, TimeSpan.FromMinutes(1)));
        options.RateLimiting.Buckets.Add(new RateLimitBucket("second", 300, TimeSpan.FromSeconds(1)));

        options.Retry.Policy = RetryPolicy.Default;
        options.Timeouts.AttemptTimeout = TimeSpan.FromSeconds(10);
        options.Timeouts.OverallTimeout = TimeSpan.FromSeconds(30);
    });
```

Each section can also be registered on its own: `AddRequestSigning`, `AddWeightedRateLimiting`, `AddRetry`, `AddSanitizedLogging`.

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

Timeouts come in two layers: `AttemptTimeout` bounds one attempt, `OverallTimeout` bounds the whole exchange including backoff waits.

---

## Logging: masked, and fail-closed

`SanitizingLoggingHandler` writes through the `ILogger` abstraction and binds to no concrete logging implementation. Sensitive query values are masked while the path and the harmless parameters survive, because debugging needs to show which endpoint was called:

```
HTTP 送出 GET https://api.example.com/fapi/v1/order?symbol=BTCUSDT&apiKey=vmPU****Eh8A&signature=****
```

Masking is `Ozakboy.Security`'s `SecretMasker`, whose default name list already covers `apiKey`, `signature`, `token`, `authorization` and friends, matched case-insensitively. Add your own through `AdditionalSensitiveParameterNames`, and register the key value itself with `RegisterKnownSecret` to catch it wherever a peer echoes it back.

**If masking fails, the value is discarded — never emitted raw.** Fail-open here would write the credential straight into the log while everything still looked fine.

---

## Failures come back as `Result<T>`

`HttpPipelineClient` wraps the assembled `HttpClient` and converts the exception path back into `Result<T>`, so callers handle one shape rather than remembering which exceptions to catch.

```csharp
var client = new HttpPipelineClient(httpClient, timeouts);

var result = await client.SendForStringAsync(request, cancellationToken);
if (!result.TryGetValue(out var body))
{
    // Category already says whether retrying is worthwhile
    if (result.Error.IsTransient) { /* back off and come round again */ }
    return result.ToFailure<Order>();
}
```

`HttpErrorMapper` does the classification: 429 is `RateLimited`, 408 is `Timeout`, every 5xx is `Unavailable` — all transient. Other 4xx codes are not: the request itself is wrong, and resending it unchanged earns the same answer plus another slice of quota. A caller cancellation maps to `Cancelled`, which is not transient — it deserves neither a retry nor an alert.

---

## Testing

`dotnet test` runs 180 tests with no network and no `Thread.Sleep`. Signing is pinned to golden vectors published in the Binance documentation, with counter-proofs that parameter order and encoding order really do change the result. Rate limiting and retry timing run on `FakeTimeProvider`.

---

## Licence

MIT. See [LICENSE](LICENSE).

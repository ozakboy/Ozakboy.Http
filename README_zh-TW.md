# Ozakboy.Http

給 `HttpClient` 用的簽章、限流、重試與脫敏日誌管線,完全建立在 Microsoft 官方套件之上。

[English](README.md) | 繁體中文

```
dotnet add package Ozakboy.Http
```

需要 .NET 10。除 Microsoft 官方套件與 `Ozakboy.*` 之外沒有其他相依。

---

## 為什麼有這個套件

自動交易引擎對 HTTP 層有四個要求:私有請求一律簽章、遵守交易所的權重配額、暫時性故障要重試但絕不能重送訂單、日誌裡不能出現 API 金鑰。一般的解法是 RestSharp、Flurl 加 Polly。

這個套件在官方的 `HttpClientFactory` 與 `DelegatingHandler` 管線上做完這四件事,相依樹裡沒有任何第三方套件。

> 關於 `Microsoft.Extensions.Http.Resilience`:名字掛 Microsoft,但相依第三方的 `Polly.Core`。判定看遞移相依而不看套件名稱,它不合格 —— 這也正是這裡的重試處理器自己寫的原因。

---

## 管線組成

四個處理器,**順序很重要**:

```
簽章 → 限流 → 重試 → 脫敏日誌 → 網路
```

順序錯了不會有任何錯誤訊息,只會在執行期表現成難以理解的行為:

- **簽章排在重試內層**:每次重試都重新簽一次,時間戳跟著變,某些服務會直接拒絕。
- **限流排在重試內層**:重試風暴不算權重,對方的配額會被打穿。
- **日誌不在最內層**:記到的是呼叫端打算送出的內容,不是真正送出去的那一份。

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

每一段也可以單獨註冊:`AddRequestSigning`、`AddWeightedRateLimiting`、`AddRetry`、`AddSanitizedLogging`。

---

## 簽章:三個會造成偶發失敗的細節

**參數順序會改變簽章。** HMAC 簽的是串起來的那一整條字串,順序一變就是另一個簽章。所以待簽參數絕不放進 `Dictionary` —— 字典的列舉順序沒有保證,一旦變動就會出現「偶爾簽章錯誤、重跑又好」這種極難追查的症狀。`QueryParameters` 只能循序附加,加入順序即簽章順序。

```csharp
var parameters = QueryParameters.CreateBuilder()
    .Add("symbol", "BTCUSDT")
    .Add("quantity", 0.001m)        // 序列化成 "0.001",不是 "1E-03"
    .Add("timestamp", timestamp)
    .Build();

using var request = new HttpRequestMessage(HttpMethod.Post, "/fapi/v1/order")
    .WithQueryParameters(parameters)
    .WithSignature()
    .WithWeight(1);
```

**先編碼再簽。** 簽的字串必須與送出的字串逐字相同。編碼一律用 `Uri.EscapeDataString`,不用 `HttpUtility.UrlEncode` —— 後者把空白編成 `+` 而非 `%20`,兩邊算出來的簽章就會不一致。

**decimal 不帶科學記號也不帶尾隨零。** 序列化一律經過 `Precision.ToPlainString`。數量若以 `1E-05` 送出,對方通常只回一句看不出原因的參數錯誤。

演算法可透過 `ISignatureAlgorithm` 抽換,預設的 `HmacSha256SignatureAlgorithm` 輸出小寫十六進位。

---

## 限流:加權、多桶、可用假時鐘測試

交易所常同時有多個時間窗的配額,而且不同端點消耗的權重不同。`RateLimitBucket` 描述一個時間窗,所有桶都足夠時請求才放行。

配額計算由 `System.Threading.RateLimiting` 的 `TokenBucketRateLimiter` 負責 —— 這裡不自研任何限流演算法。自己處理的是**時間**:那個原語的補充機制綁死在真實時鐘上(即使關掉自動補充也一樣),多桶行為因此測不出來。`WeightedRateLimiter` 改由 `TimeProvider` 驅動補充,測試只要推進 `FakeTimeProvider`,不必真的等。

權重超過最小配額桶的請求會立刻失敗而不是無限等待;等待超過 `AcquisitionTimeout` 則以 `ErrorCategory.RateLimited` 失敗,而且請求從未離開本機。

---

## 重試:非冪等的請求絕不重試

這是整個套件最重要的部分。逾時只代表「沒收到回應」,**不代表**「對方沒收到請求」—— 連線可能是在回應回來的路上斷的,那筆訂單早就成立了。重送的代價就是重複下單,而造成的部位偏差往往要到對帳時才被發現。

- 安全方法(`GET`、`HEAD`、`OPTIONS`、`TRACE`)會重試。
- `POST`、`DELETE`、`PUT`、`PATCH` 預設**不重試**。
- `request.AsIdempotent()` 明確開啟,`request.AsNonIdempotent()` 明確關閉。

做成明確宣告是刻意的。用布林值時,預設的 `false` 和「有人想過並決定不重試」長得一模一樣,程式碼審查時分不出來。

退避間隔由 `RetryPolicy.GetDelay` 計算。回應若帶 `Retry-After`,以對方指定的時間為準(上限為 `MaxRetryAfter`)—— 伺服器知道自己的封鎖窗口還剩多長,提早重送只會把封鎖時間延長。

「值不值得重試」完全由策略決定。設了 `RetryPolicy.RetryPredicate` 就是**取代**內建的暫時性判斷,不是再加一個條件;而傳進去的錯誤上已經備齊做這個判斷所需要的資料:

```csharp
var policy = RetryPolicy.Default with
{
    // 會說「什麼時候再來」的 429 值得再試一次;不說的那種,通常是位址被封了。
    RetryPredicate = error => error.TryGetDecimal(HttpErrorDataKeys.RetryAfterSeconds, out _),
};
```

本來值得重試、但次數已經用盡的失敗,交回去時會改標成 `ErrorCategory.Exhausted`:代碼與訊息保留,但 `IsTransient` 變成 false —— 呼叫端自己的重試層才不會把同一個故障再乘一輪。

逾時分兩層:`AttemptTimeout` 管單次嘗試,`OverallTimeout` 管含退避等待在內的整趟請求。

---

## 日誌:遮罩,而且失敗封閉

`SanitizingLoggingHandler` 只相依 `ILogger` 抽象,不綁定任何具體日誌實作。敏感的 query 值會被遮掉,但路徑與非敏感參數保留 —— 除錯時必須看得出打了哪個端點:

```
HTTP 送出 GET https://api.example.com/fapi/v1/order?symbol=BTCUSDT&apiKey=vmPU****Eh8A&signature=****
```

遮罩用的是 `Ozakboy.Security` 的 `SecretMasker`,預設名單已涵蓋 `apiKey`、`signature`、`token`、`authorization` 等常見名稱,比對忽略大小寫。專屬名稱用 `AdditionalSensitiveParameterNames` 補上;把金鑰值本身用 `RegisterKnownSecret` 登記起來,對方 echo 回錯誤訊息裡的那種也擋得住。

**遮罩失敗時整段捨棄,絕不原樣輸出。** 這裡若 fail-open,等於把憑證直接寫進日誌,而且畫面上一切看起來都正常。

---

## 失敗一律回傳 `Result<T>`

`HttpPipelineClient` 包住組好的 `HttpClient`,把例外路徑收斂回 `Result<T>`,呼叫端只需要處理一種形狀,不必記得哪些例外要攔。

```csharp
var client = new HttpPipelineClient(httpClient, timeouts);

var result = await client.SendForStringAsync(request, cancellationToken);
if (!result.TryGetValue(out var body))
{
    // 錯誤分類已經能回答「值不值得重試」
    if (result.Error.IsTransient) { /* 退一步,稍後再來 */ }
    return result.ToFailure<Order>();
}
```

分類由 `HttpErrorMapper` 負責:429 是 `RateLimited`、408 是 `Timeout`、所有 5xx 是 `Unavailable`,這三類都是暫時性的。其餘 4xx 不是:請求本身有問題,原封不動重送只會得到同一個答案,還多消耗一次配額。呼叫端主動取消對映成 `Cancelled`,不是暫時性 —— 它既不該被重試也不該告警。

診斷資料放在 `Error.Data`,鍵名集中在 `HttpErrorDataKeys`,而且讀回來是有型別的:`error.TryGetInt64(HttpErrorDataKeys.StatusCode, out var status)`、`error.TryGetDecimal(HttpErrorDataKeys.RetryAfterSeconds, out var seconds)`。下游不必把字串再 parse 回數值,也就沒有機會漏掉 `InvariantCulture`。

不走門面、直接用 `HttpClient` 的呼叫端,攔的是 `Ozakboy.Core.Abstractions` 的 `ResultException`。它是 Ozakboy 各套件共用的那一個載具,專門在「簽章由 BCL 決定、`Result<T>` 過不去」的邊界上攜帶 `Error`;`Error` 屬性保證非 null,型別繼承自 `InvalidOperationException`。要攔的例外只有這一種,不會每個套件各一種。

---

## 測試

`dotnet test` 跑 188 個測試,全程不連網、沒有任何 `Thread.Sleep`。簽章以幣安官方文件公佈的黃金向量鎖死,另有反證測試證明參數順序與編碼順序確實會改變結果。限流與重試的時間行為跑在 `FakeTimeProvider` 上。

---

## 授權

MIT。見 [LICENSE](LICENSE)。

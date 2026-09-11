using Ozakboy.Http.Signing;

namespace Ozakboy.Http.Tests;

/// <summary>
/// 簽章的黃金向量測試:固定輸入必須得到固定輸出。
/// Golden-vector tests for signing: a fixed input must always produce a fixed output.
/// </summary>
/// <remarks>
/// <para>
/// 測試資料取自幣安官方文件公開的示範金鑰與示範簽章(文件明言僅供示範用途)。
/// 這是本套件唯一引用特定服務的地方,而且只出現在測試資料裡 —— 產品程式碼不含任何交易所專屬邏輯。
/// The test data comes from the demonstration keys and signatures published in Binance's own documentation,
/// which states they exist for illustration only. This is the only place in the package that references a
/// specific service, and it appears in test data alone; the product code contains no exchange-specific logic.
/// </para>
/// <para>
/// 來源(現貨,Endpoint security type):
/// https://developers.binance.com/docs/binance-spot-api-docs/rest-api/endpoint-security-type
/// 來源(U 本位永續合約,General info):
/// https://developers.binance.com/docs/derivatives/usds-margined-futures/general-info
/// </para>
/// </remarks>
[TestClass]
public sealed class SignatureGoldenVectorTests
{
    // 幣安現貨文件的示範向量。Demonstration vector from the Binance spot documentation.
    private const string SpotSecretKey = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";
    private const string SpotQueryString = "symbol=LTCBTC&side=BUY&type=LIMIT&timeInForce=GTC&quantity=1&price=0.1&recvWindow=5000&timestamp=1499827319559";
    private const string SpotExpectedSignature = "c8db56825ae71d6d79447849e617115f4a920fa2acdcab2b053c4b2838bd6b71";

    // 幣安 U 本位永續合約文件的示範向量。Demonstration vector from the Binance USDⓈ-M futures documentation.
    private const string FuturesSecretKey = "2b5eb11e18796d12d88f13dc27dbbd02c2cc51ff7059765ed9821957d82bb4d9";
    private const string FuturesQueryString = "symbol=BTCUSDT&side=BUY&type=LIMIT&quantity=1&price=9000&timeInForce=GTC&recvWindow=5000&timestamp=1591702613943";
    private const string FuturesExpectedSignature = "3c661234138461fcc7a7d8746c6558c9842d4e10870d2ecbedf7777cad694af9";

    [TestMethod]
    public void Sign_SpotDocumentationVector_MatchesPublishedSignature()
    {
        var result = HmacSha256SignatureAlgorithm.Instance.Sign(SpotQueryString, SpotSecretKey);

        Assert.IsTrue(result.TryGetValue(out var signature), "簽章不應失敗。Signing should not fail.");
        Assert.AreEqual(SpotExpectedSignature, signature, "與幣安現貨文件公佈的簽章不符。Does not match the signature published in the Binance spot docs.");
    }

    [TestMethod]
    public void Sign_FuturesDocumentationVector_MatchesPublishedSignature()
    {
        var result = HmacSha256SignatureAlgorithm.Instance.Sign(FuturesQueryString, FuturesSecretKey);

        Assert.IsTrue(result.TryGetValue(out var signature), "簽章不應失敗。Signing should not fail.");
        Assert.AreEqual(FuturesExpectedSignature, signature, "與幣安合約文件公佈的簽章不符。Does not match the signature published in the Binance futures docs.");
    }

    [TestMethod]
    public void QueryParameters_BuiltInDocumentedOrder_ReproducesTheGoldenVector()
    {
        // 用本套件的有序容器重建文件裡那串參數,再簽一次 —— 端到端證明容器沒有改動順序或編碼。
        // Rebuilding the documented parameter string through this package's ordered container and signing it
        // again proves end to end that the container alters neither order nor encoding.
        var parameters = QueryParameters.CreateBuilder()
            .Add("symbol", "LTCBTC")
            .Add("side", "BUY")
            .Add("type", "LIMIT")
            .Add("timeInForce", "GTC")
            .Add("quantity", 1m)
            .Add("price", 0.1m)
            .Add("recvWindow", 5000L)
            .Add("timestamp", 1499827319559L)
            .Build();

        Assert.AreEqual(SpotQueryString, parameters.ToQueryString());

        var result = HmacSha256SignatureAlgorithm.Instance.Sign(parameters.ToQueryString(), SpotSecretKey);

        Assert.IsTrue(result.TryGetValue(out var signature));
        Assert.AreEqual(SpotExpectedSignature, signature);
    }

    [TestMethod]
    public void Sign_ParameterOrderChanged_ProducesADifferentSignature()
    {
        // 反證:證明順序真的會影響簽章。這正是待簽參數不能放進 Dictionary 的原因 ——
        // 字典的列舉順序沒有保證,一旦變動就會得到下面這種「看起來沒改什麼卻簽不過」的結果。
        // Counter-proof that order really does change the signature. This is precisely why signing parameters
        // must not live in a Dictionary: its enumeration order is unspecified, and when it shifts the result
        // is exactly this — nothing apparently changed, yet the signature no longer matches.
        var original = QueryParameters.CreateBuilder()
            .Add("symbol", "LTCBTC")
            .Add("side", "BUY")
            .Build();

        var swapped = QueryParameters.CreateBuilder()
            .Add("side", "BUY")
            .Add("symbol", "LTCBTC")
            .Build();

        Assert.IsTrue(HmacSha256SignatureAlgorithm.Instance.Sign(original.ToQueryString(), SpotSecretKey).TryGetValue(out var first));
        Assert.IsTrue(HmacSha256SignatureAlgorithm.Instance.Sign(swapped.ToQueryString(), SpotSecretKey).TryGetValue(out var second));

        Assert.AreNotEqual(first, second, "參數順序不同卻得到相同簽章,表示順序沒有真的進入待簽字串。Identical signatures for different orders would mean the order never reached the signed string.");
    }

    [TestMethod]
    public void Sign_EncodingBeforeVersusAfterSigning_ProducesDifferentSignatures()
    {
        // 反證:「先編碼再簽」與「先簽再編碼」是兩件不同的事。
        // 值裡含有需要編碼的字元時,兩條路徑的待簽字串不同,簽章自然不同;
        // 送出的是編碼後的字串,所以只有「先編碼再簽」那條是對的。
        // Counter-proof that encoding before signing and signing before encoding are not the same operation.
        // When a value contains characters that need escaping, the two paths sign different strings and get
        // different results; what goes on the wire is the encoded form, so only the encode-first path is right.
        const string rawValue = "BTC USDT+1";

        var encodedFirst = $"symbol={Uri.EscapeDataString(rawValue)}";
        var rawUnencoded = $"symbol={rawValue}";

        Assert.AreEqual("symbol=BTC%20USDT%2B1", encodedFirst, "空白必須編成 %20 而非 +。A space must encode to %20, never to +.");

        Assert.IsTrue(HmacSha256SignatureAlgorithm.Instance.Sign(encodedFirst, SpotSecretKey).TryGetValue(out var signatureOfEncoded));
        Assert.IsTrue(HmacSha256SignatureAlgorithm.Instance.Sign(rawUnencoded, SpotSecretKey).TryGetValue(out var signatureOfRaw));

        Assert.AreNotEqual(signatureOfRaw, signatureOfEncoded);

        // 而本套件產生的待簽字串走的是前者。
        // And the canonical string this package produces takes the first path.
        var parameters = QueryParameters.CreateBuilder().Add("symbol", rawValue).Build();
        Assert.AreEqual(encodedFirst, parameters.ToQueryString());
    }

    [TestMethod]
    public void Sign_MissingSecret_ReturnsValidationFailure()
    {
        var result = HmacSha256SignatureAlgorithm.Instance.Sign(SpotQueryString, string.Empty);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(HttpErrorCodes.SigningSecretMissing, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Validation, result.Error!.Category);
        Assert.IsFalse(result.Error!.IsTransient, "金鑰缺漏不是暫時性問題,重試沒有意義。A missing secret is not transient; retrying is pointless.");
    }

    [TestMethod]
    public void Sign_Result_IsLowercaseHexadecimal()
    {
        Assert.IsTrue(HmacSha256SignatureAlgorithm.Instance.Sign(SpotQueryString, SpotSecretKey).TryGetValue(out var signature));

        Assert.AreEqual(64, signature.Length, "SHA-256 的十六進位表示固定 64 個字元。A SHA-256 digest is always 64 hex characters.");
        Assert.AreEqual(signature.ToLowerInvariant(), signature, "簽章必須是小寫十六進位。The signature must be lowercase hexadecimal.");
    }

    [TestMethod]
    public void Name_IsTheAlgorithmIdentifier() =>
        Assert.AreEqual("HMAC-SHA256", HmacSha256SignatureAlgorithm.Instance.Name);

    [TestMethod]
    public void Sign_NullPayload_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => HmacSha256SignatureAlgorithm.Instance.Sign(null!, SpotSecretKey));
}

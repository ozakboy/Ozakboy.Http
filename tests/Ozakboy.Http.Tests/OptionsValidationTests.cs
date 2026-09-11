using Ozakboy.Http.Logging;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;

namespace Ozakboy.Http.Tests;

[TestClass]
public sealed class OptionsValidationTests
{
    [TestMethod]
    public void SigningOptions_Defaults_AreValid() =>
        Assert.IsTrue(new SigningOptions().Validate().IsSuccess);

    [TestMethod]
    public void SigningOptions_NoAlgorithm_IsInvalid() =>
        AssertInvalid(new SigningOptions { Algorithm = null! }.Validate());

    [TestMethod]
    public void SigningOptions_BlankSignatureParameterName_IsInvalid() =>
        AssertInvalid(new SigningOptions { SignatureParameterName = "  " }.Validate());

    [TestMethod]
    public void SigningOptions_BlankApiKeyHeaderWhileSendingIt_IsInvalid() =>
        AssertInvalid(new SigningOptions { ApiKeyHeaderName = string.Empty }.Validate());

    [TestMethod]
    public void SigningOptions_BlankApiKeyHeaderWhenNotSendingIt_IsValid() =>
        Assert.IsTrue(new SigningOptions { ApiKeyHeaderName = string.Empty, SendApiKeyHeader = false }.Validate().IsSuccess);

    [TestMethod]
    public void RateLimitOptions_NoBuckets_IsInvalid() =>
        AssertInvalid(new RateLimitOptions().Validate());

    [TestMethod]
    public void RateLimitOptions_NullBucket_IsInvalid()
    {
        var options = new RateLimitOptions();
        options.Buckets.Add(null!);

        AssertInvalid(options.Validate());
    }

    [TestMethod]
    public void RateLimitOptions_NonPositiveDefaultWeight_IsInvalid()
    {
        var options = new RateLimitOptions { DefaultWeight = 0 };
        options.Buckets.Add(new RateLimitBucket("b", 10, TimeSpan.FromSeconds(1)));

        AssertInvalid(options.Validate());
    }

    [TestMethod]
    public void RateLimitOptions_NonPositiveAcquisitionTimeout_IsInvalid()
    {
        var options = new RateLimitOptions { AcquisitionTimeout = TimeSpan.Zero };
        options.Buckets.Add(new RateLimitBucket("b", 10, TimeSpan.FromSeconds(1)));

        AssertInvalid(options.Validate());
    }

    [TestMethod]
    public void RateLimitOptions_DefaultWeightAboveTheSmallestBucket_IsInvalid()
    {
        // 這個設定的意思是「每個請求都不可能通過」,而它的症狀是整條管線靜默停擺 ——
        // 在設定階段就擋下來,比上線後才發現好得多。
        // This configuration means no request can ever pass, and its symptom is the whole pipeline silently
        // stalling. Rejecting it at configuration time beats discovering it in production.
        var options = new RateLimitOptions { DefaultWeight = 11 };
        options.Buckets.Add(new RateLimitBucket("b", 10, TimeSpan.FromSeconds(1)));

        AssertInvalid(options.Validate());
    }

    [TestMethod]
    public void RetryOptions_Defaults_AreValid() =>
        Assert.IsTrue(new RetryOptions().Validate().IsSuccess);

    [TestMethod]
    public void RetryOptions_NoPolicy_IsInvalid() =>
        AssertInvalid(new RetryOptions { Policy = null! }.Validate());

    [TestMethod]
    public void RetryOptions_NonPositiveRetryAfterCeiling_IsInvalid() =>
        AssertInvalid(new RetryOptions { MaxRetryAfter = TimeSpan.Zero }.Validate());

    [TestMethod]
    public void RetryOptions_NegativeSnippetLength_IsInvalid() =>
        AssertInvalid(new RetryOptions { ErrorBodySnippetLength = -1 }.Validate());

    [TestMethod]
    public void HttpTimeoutOptions_Defaults_AreValid() =>
        Assert.IsTrue(new HttpTimeoutOptions().Validate().IsSuccess);

    [TestMethod]
    public void HttpTimeoutOptions_NonPositiveAttemptTimeout_IsInvalid() =>
        AssertInvalid(new HttpTimeoutOptions { AttemptTimeout = TimeSpan.Zero }.Validate());

    [TestMethod]
    public void HttpTimeoutOptions_NonPositiveOverallTimeout_IsInvalid() =>
        AssertInvalid(new HttpTimeoutOptions { OverallTimeout = TimeSpan.Zero }.Validate());

    [TestMethod]
    public void HttpTimeoutOptions_OverallShorterThanAttempt_IsInvalid() =>
        AssertInvalid(new HttpTimeoutOptions
        {
            AttemptTimeout = TimeSpan.FromMinutes(1),
            OverallTimeout = TimeSpan.FromSeconds(1),
        }.Validate());

    [TestMethod]
    public void RequestLoggingOptions_Defaults_AreValid() =>
        Assert.IsTrue(new RequestLoggingOptions().Validate().IsSuccess);

    [TestMethod]
    public void RequestLoggingOptions_NonPositiveBodyLength_IsInvalid() =>
        AssertInvalid(new RequestLoggingOptions { MaxBodyLength = 0 }.Validate());

    [TestMethod]
    public void RequestLoggingOptions_BlankAdditionalName_IsInvalid()
    {
        var options = new RequestLoggingOptions();
        options.AdditionalSensitiveParameterNames.Add("  ");

        AssertInvalid(options.Validate());
    }

    [TestMethod]
    public void HttpPipelineOptions_DefaultsWithRateLimitingOn_AreInvalidUntilABucketIsAdded()
    {
        var options = new HttpPipelineOptions();
        AssertInvalid(options.Validate());

        options.RateLimiting.Buckets.Add(new RateLimitBucket("b", 10, TimeSpan.FromSeconds(1)));
        Assert.IsTrue(options.Validate().IsSuccess);
    }

    [TestMethod]
    public void HttpPipelineOptions_BothSectionsDisabled_IsValid()
    {
        var options = new HttpPipelineOptions
        {
            EnableSigning = false,
            EnableRateLimiting = false,
        };

        Assert.IsTrue(options.Validate().IsSuccess);
    }

    [TestMethod]
    public void HttpPipelineOptions_InvalidSigning_IsReported()
    {
        var options = new HttpPipelineOptions { EnableRateLimiting = false };
        options.Signing.SignatureParameterName = " ";

        AssertInvalid(options.Validate());
    }

    [TestMethod]
    public void HttpPipelineOptions_InvalidLogging_IsReported()
    {
        var options = new HttpPipelineOptions { EnableSigning = false, EnableRateLimiting = false };
        options.Logging.MaxBodyLength = 0;

        AssertInvalid(options.Validate());
    }

    private static void AssertInvalid(Result result)
    {
        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(HttpErrorCodes.InvalidOptions, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Validation, result.Error!.Category);
    }
}

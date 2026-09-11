using System.Net.Http.Headers;
using Ozakboy.Http.Retry;
using Ozakboy.Http.Signing;

namespace Ozakboy.Http.Tests;

[TestClass]
public sealed class RequestDeclarationTests
{
    [TestMethod]
    public void Declarations_RoundTripThroughRequestOptions()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        var parameters = QueryParameters.CreateBuilder().Add("a", "1").Build();

        request.WithQueryParameters(parameters).WithSignature().WithWeight(12).AsIdempotent();

        Assert.AreSame(parameters, request.GetQueryParameters());
        Assert.IsTrue(request.RequiresSignature());
        Assert.AreEqual(12, request.GetWeight());
        Assert.AreEqual(RequestIdempotency.Idempotent, request.GetIdempotency());
    }

    [TestMethod]
    public void Declarations_Unset_ReturnTheNeutralDefaults()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");

        Assert.IsNull(request.GetQueryParameters());
        Assert.IsFalse(request.RequiresSignature());
        Assert.IsNull(request.GetWeight());
        Assert.IsNull(request.GetRetryPolicy());
        Assert.AreEqual(RequestIdempotency.Inferred, request.GetIdempotency());
    }

    [TestMethod]
    public void AsNonIdempotent_OverridesAnEarlierIdempotentDeclaration()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/");

        request.AsIdempotent().AsNonIdempotent();

        Assert.AreEqual(RequestIdempotency.NonIdempotent, request.GetIdempotency());
    }

    [TestMethod]
    public void WithRetryPolicy_IsReadBack()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");

        request.WithRetryPolicy(RetryPolicy.NoRetry);

        Assert.AreSame(RetryPolicy.NoRetry, request.GetRetryPolicy());
    }

    [TestMethod]
    public void WithWeight_NonPositive_Throws()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => request.WithWeight(0));
    }

    [TestMethod]
    public void Extensions_NullRequest_Throw()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => HttpRequestMessageExtensions.WithSignature(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => HttpRequestMessageExtensions.RequiresSignature(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => HttpRequestMessageExtensions.GetWeight(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => HttpRequestMessageExtensions.GetIdempotency(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => HttpRequestMessageExtensions.GetQueryParameters(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => HttpRequestMessageExtensions.GetRetryPolicy(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => HttpRequestMessageExtensions.AsIdempotent(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => HttpRequestMessageExtensions.AsNonIdempotent(null!));
    }

    [TestMethod]
    public async Task Clone_CarriesHeadersContentAndDeclarations()
    {
        using var original = new HttpRequestMessage(HttpMethod.Post, "https://example.test/api")
        {
            Content = new StringContent("payload"),
        };
        original.Headers.TryAddWithoutValidation("X-Custom", "value");
        original.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        original.WithWeight(9).AsIdempotent();

        var buffered = await HttpRequestCloner.BufferContentAsync(original, CancellationToken.None);
        using var clone = HttpRequestCloner.Clone(original, buffered);

        Assert.AreEqual(original.Method, clone.Method);
        Assert.AreEqual(original.RequestUri, clone.RequestUri);
        Assert.AreEqual("value", clone.Headers.GetValues("X-Custom").Single());
        Assert.AreEqual("payload", await clone.Content!.ReadAsStringAsync(CancellationToken.None));
        Assert.AreEqual("application/x-www-form-urlencoded", clone.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual(9, clone.GetWeight());
        Assert.AreEqual(RequestIdempotency.Idempotent, clone.GetIdempotency());
    }

    [TestMethod]
    public async Task BufferContentAsync_NoContent_ReturnsNull()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api");

        Assert.IsNull(await HttpRequestCloner.BufferContentAsync(request, CancellationToken.None));

        using var clone = HttpRequestCloner.Clone(request, null);
        Assert.IsNull(clone.Content);
    }
}

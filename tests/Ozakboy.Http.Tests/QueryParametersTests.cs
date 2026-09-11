using Ozakboy.Http.Signing;

namespace Ozakboy.Http.Tests;

[TestClass]
public sealed class QueryParametersTests
{
    private static readonly string[] ExpectedInsertionOrder = ["z", "a", "m"];

    [TestMethod]
    public void Build_PreservesInsertionOrder()
    {
        var parameters = QueryParameters.CreateBuilder()
            .Add("z", "1")
            .Add("a", "2")
            .Add("m", "3")
            .Build();

        CollectionAssert.AreEqual(
            ExpectedInsertionOrder,
            parameters.Items.Select(item => item.Key).ToArray());
        Assert.AreEqual("z=1&a=2&m=3", parameters.ToQueryString());
    }

    [TestMethod]
    public void ToQueryString_EncodesSpaceAsPercentTwenty()
    {
        var parameters = QueryParameters.CreateBuilder().Add("note", "a b").Build();

        Assert.AreEqual("note=a%20b", parameters.ToQueryString());
        Assert.IsFalse(parameters.ToQueryString().Contains('+', StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToQueryString_EncodesReservedCharactersInNamesAndValues()
    {
        var parameters = QueryParameters.CreateBuilder().Add("a&b", "c=d&e").Build();

        Assert.AreEqual("a%26b=c%3Dd%26e", parameters.ToQueryString());
    }

    [TestMethod]
    public void Add_Decimal_SerialisesWithoutExponentOrTrailingZeros()
    {
        var parameters = QueryParameters.CreateBuilder()
            .Add("tiny", 0.00001m)
            .Add("padded", 0.10000m)
            .Add("whole", 100.000m)
            .Build();

        Assert.AreEqual("tiny=0.00001&padded=0.1&whole=100", parameters.ToQueryString());
    }

    [TestMethod]
    public void Add_BooleanAndLong_UseInvariantForms()
    {
        var parameters = QueryParameters.CreateBuilder()
            .Add("flag", true)
            .Add("other", false)
            .Add("count", 1_499_827_319_559L)
            .Build();

        Assert.AreEqual("flag=true&other=false&count=1499827319559", parameters.ToQueryString());
    }

    [TestMethod]
    public void AddIfNotNull_SkipsNullValues()
    {
        var parameters = QueryParameters.CreateBuilder()
            .AddIfNotNull("a", (string?)null)
            .AddIfNotNull("b", (decimal?)null)
            .AddIfNotNull("c", (long?)null)
            .AddIfNotNull("d", "kept")
            .AddIfNotNull("e", (decimal?)1.5m)
            .AddIfNotNull("f", (long?)7L)
            .Build();

        Assert.AreEqual("d=kept&e=1.5&f=7", parameters.ToQueryString());
    }

    [TestMethod]
    public void Empty_HasNoParametersAndProducesEmptyString()
    {
        Assert.AreEqual(0, QueryParameters.Empty.Count);
        Assert.AreEqual(string.Empty, QueryParameters.Empty.ToQueryString());
    }

    [TestMethod]
    public void Append_ReturnsNewInstanceAndLeavesTheOriginalAlone()
    {
        var original = QueryParameters.CreateBuilder().Add("a", "1").Build();

        var appended = original.Append("b", "2");

        Assert.AreEqual("a=1", original.ToQueryString());
        Assert.AreEqual("a=1&b=2", appended.ToQueryString());
        Assert.AreEqual(1, original.Count);
        Assert.AreEqual(2, appended.Count);
    }

    [TestMethod]
    public void ToString_MatchesToQueryString()
    {
        var parameters = QueryParameters.CreateBuilder().Add("a", "1").Build();

        Assert.AreEqual(parameters.ToQueryString(), parameters.ToString());
    }

    [TestMethod]
    public void Builder_Count_TracksAdditions()
    {
        var builder = QueryParameters.CreateBuilder();
        Assert.AreEqual(0, builder.Count);

        builder.Add("a", "1").Add("b", "2");
        Assert.AreEqual(2, builder.Count);
    }

    [TestMethod]
    public void Add_BlankName_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => QueryParameters.CreateBuilder().Add("  ", "1"));

    [TestMethod]
    public void Add_NullValue_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => QueryParameters.CreateBuilder().Add("a", (string)null!));

    [TestMethod]
    public void Append_BlankName_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => QueryParameters.Empty.Append(string.Empty, "1"));

    [TestMethod]
    public void Append_NullValue_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => QueryParameters.Empty.Append("a", null!));

    [TestMethod]
    public void DuplicateNames_AreKeptInOrder()
    {
        // 同名參數在某些 API 是合法的(例如多值篩選),容器不該自作主張去重 ——
        // 字典會,而那正是簽章對不起來的來源之一。
        // Duplicate names are legal in some APIs (multi-value filters), and the container must not silently
        // deduplicate them — a dictionary would, and that is one more way signatures stop matching.
        var parameters = QueryParameters.CreateBuilder()
            .Add("id", "1")
            .Add("id", "2")
            .Build();

        Assert.AreEqual("id=1&id=2", parameters.ToQueryString());
    }
}

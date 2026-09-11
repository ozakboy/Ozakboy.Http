using System.Globalization;
using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http.Signing;

/// <summary>
/// 循序組裝 <see cref="QueryParameters"/> 的建構器。加入順序即簽章順序。
/// Builds a <see cref="QueryParameters"/> in order. The order values are added is the signing order.
/// </summary>
/// <remarks>
/// 這個型別不是執行緒安全的;它的用途是在單一請求的組裝過程中短暫存在。
/// This type is not thread-safe; it is meant to live only while a single request is being assembled.
/// </remarks>
public sealed class QueryParametersBuilder
{
    private readonly List<KeyValuePair<string, string>> _items = [];

    /// <summary>
    /// 目前已加入的參數個數。
    /// How many parameters have been added so far.
    /// </summary>
    public int Count => _items.Count;

    /// <summary>
    /// 加入字串參數。
    /// Adds a string parameter.
    /// </summary>
    /// <param name="name">參數名,不可為空白。The parameter name; must not be blank.</param>
    /// <param name="value">參數值,允許空字串但不允許 <see langword="null"/>。The value; empty is allowed, <see langword="null"/> is not.</param>
    /// <returns>建構器本身,便於串接。The builder, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> 為空白時擲出。Thrown when <paramref name="name"/> is blank.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="value"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="value"/> is <see langword="null"/>.
    /// </exception>
    public QueryParametersBuilder Add(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        _items.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    /// <summary>
    /// 加入十進位數值參數,序列化為不含科學記號與尾隨零的字串。
    /// Adds a decimal parameter, serialised without exponent notation or trailing zeros.
    /// </summary>
    /// <param name="name">參數名。The parameter name.</param>
    /// <param name="value">數值。The value.</param>
    /// <returns>建構器本身。The builder.</returns>
    /// <remarks>
    /// 價格與數量若以 <c>1E-05</c> 或 <c>0.10000</c> 這種形式送出,對方通常只回一句參數錯誤或簽章錯誤,
    /// 完全看不出真正原因。序列化一律經 <see cref="Precision.ToPlainString(decimal)"/>。
    /// A price or quantity sent as <c>1E-05</c> or <c>0.10000</c> usually comes back as an opaque parameter or
    /// signature error that gives no hint of the real cause. Serialisation always goes through
    /// <see cref="Precision.ToPlainString(decimal)"/>.
    /// </remarks>
    public QueryParametersBuilder Add(string name, decimal value) =>
        Add(name, Precision.ToPlainString(value));

    /// <summary>
    /// 加入整數參數。
    /// Adds an integer parameter.
    /// </summary>
    /// <param name="name">參數名。The parameter name.</param>
    /// <param name="value">數值。The value.</param>
    /// <returns>建構器本身。The builder.</returns>
    public QueryParametersBuilder Add(string name, long value) =>
        Add(name, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// 加入布林參數,序列化為 <c>true</c> 或 <c>false</c>(小寫)。
    /// Adds a boolean parameter, serialised as lowercase <c>true</c> or <c>false</c>.
    /// </summary>
    /// <param name="name">參數名。The parameter name.</param>
    /// <param name="value">數值。The value.</param>
    /// <returns>建構器本身。The builder.</returns>
    public QueryParametersBuilder Add(string name, bool value) =>
        Add(name, value ? "true" : "false");

    /// <summary>
    /// 值不為 <see langword="null"/> 時才加入。
    /// Adds the parameter only when the value is not <see langword="null"/>.
    /// </summary>
    /// <param name="name">參數名。The parameter name.</param>
    /// <param name="value">可為 <see langword="null"/> 的值。The value, which may be <see langword="null"/>.</param>
    /// <returns>建構器本身。The builder.</returns>
    public QueryParametersBuilder AddIfNotNull(string name, string? value) =>
        value is null ? this : Add(name, value);

    /// <summary>
    /// 值不為 <see langword="null"/> 時才加入。
    /// Adds the parameter only when the value is not <see langword="null"/>.
    /// </summary>
    /// <param name="name">參數名。The parameter name.</param>
    /// <param name="value">可為 <see langword="null"/> 的值。The value, which may be <see langword="null"/>.</param>
    /// <returns>建構器本身。The builder.</returns>
    public QueryParametersBuilder AddIfNotNull(string name, decimal? value) =>
        value is null ? this : Add(name, value.Value);

    /// <summary>
    /// 值不為 <see langword="null"/> 時才加入。
    /// Adds the parameter only when the value is not <see langword="null"/>.
    /// </summary>
    /// <param name="name">參數名。The parameter name.</param>
    /// <param name="value">可為 <see langword="null"/> 的值。The value, which may be <see langword="null"/>.</param>
    /// <returns>建構器本身。The builder.</returns>
    public QueryParametersBuilder AddIfNotNull(string name, long? value) =>
        value is null ? this : Add(name, value.Value);

    /// <summary>
    /// 產出不可變的參數序列。
    /// Produces the immutable parameter sequence.
    /// </summary>
    /// <returns>依加入順序排列的參數。The parameters in the order they were added.</returns>
    public QueryParameters Build() => new([.. _items]);
}

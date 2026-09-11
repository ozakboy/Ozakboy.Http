using System.Text;

namespace Ozakboy.Http.Signing;

/// <summary>
/// 待簽名的請求參數:順序固定、不可變的名值對序列。
/// The request parameters to be signed: an ordered, immutable sequence of name/value pairs.
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼不是 <see cref="Dictionary{TKey, TValue}"/>。</b>
/// HMAC 簽的是把參數串起來的那一整條字串,所以參數順序一改,簽章就完全不同。
/// <see cref="Dictionary{TKey, TValue}"/> 的列舉順序沒有任何保證 —— 同一份參數在不同的插入歷程、
/// 不同的執行期版本下可能以不同順序列舉。用字典承載待簽參數的症狀是「偶爾簽章錯誤、重跑又好」,
/// 這種錯誤在交易系統裡極難追查。這個型別的存在就是把那條路封死:參數只能循序加入,順序即是簽章順序。
/// <b>Why this is not a <see cref="Dictionary{TKey, TValue}"/>.</b> An HMAC signs the single concatenated
/// string built from the parameters, so changing their order changes the signature entirely.
/// <see cref="Dictionary{TKey, TValue}"/> guarantees no enumeration order: the same parameters can enumerate
/// differently depending on insertion history or runtime version. Carrying signing parameters in a dictionary
/// shows up as intermittent signature errors that disappear on retry — close to untraceable in a trading
/// system. This type exists to close that door: parameters can only be appended, and that order is the
/// signing order.
/// </para>
/// <para>
/// <b>先編碼再簽。</b><see cref="ToQueryString"/> 產生的字串就是實際送出的字串,兩者必須逐字相同。
/// 編碼一律使用 <see cref="Uri.EscapeDataString(string)"/>,不使用 <c>HttpUtility.UrlEncode</c> ——
/// 後者把空白編成 <c>+</c> 而非 <c>%20</c>,兩邊算出來的簽章就會不一致。
/// <b>Encode first, then sign.</b> The string produced by <see cref="ToQueryString"/> is the exact string that
/// goes on the wire; the two must match byte for byte. Encoding always uses
/// <see cref="Uri.EscapeDataString(string)"/> rather than <c>HttpUtility.UrlEncode</c>, which encodes a space
/// as <c>+</c> instead of <c>%20</c> and would make the two sides disagree.
/// </para>
/// </remarks>
public sealed class QueryParameters
{
    private readonly KeyValuePair<string, string>[] _items;

    internal QueryParameters(KeyValuePair<string, string>[] items)
    {
        _items = items;
    }

    /// <summary>
    /// 不含任何參數的實例。
    /// An instance with no parameters.
    /// </summary>
    public static QueryParameters Empty { get; } = new([]);

    /// <summary>
    /// 依加入順序排列的參數。
    /// The parameters in the order they were added.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Items => _items;

    /// <summary>
    /// 參數個數。
    /// The number of parameters.
    /// </summary>
    public int Count => _items.Length;

    /// <summary>
    /// 建立一個建構器。
    /// Creates a builder.
    /// </summary>
    /// <returns>空的建構器。An empty builder.</returns>
    public static QueryParametersBuilder CreateBuilder() => new();

    /// <summary>
    /// 串成已百分號編碼的 query 字串(不含前導的 <c>?</c>)。這同時是待簽字串與實際送出的字串。
    /// Joins the parameters into a percent-encoded query string without a leading <c>?</c>. This is both the
    /// canonical string to sign and the string actually sent.
    /// </summary>
    /// <returns>
    /// 形如 <c>symbol=BTCUSDT&amp;price=0.1</c> 的字串;沒有參數時回傳空字串。
    /// A string such as <c>symbol=BTCUSDT&amp;price=0.1</c>; an empty string when there are no parameters.
    /// </returns>
    public string ToQueryString()
    {
        if (_items.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < _items.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(_items[i].Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(_items[i].Value));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 在尾端附加一個參數,回傳新的實例;原實例不變。
    /// Returns a new instance with one parameter appended; the original is unchanged.
    /// </summary>
    /// <param name="name">參數名。The parameter name.</param>
    /// <param name="value">參數值。The parameter value.</param>
    /// <returns>附加後的新實例。A new instance with the parameter appended.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> 為空白時擲出。Thrown when <paramref name="name"/> is blank.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="value"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="value"/> is <see langword="null"/>.
    /// </exception>
    public QueryParameters Append(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        var appended = new KeyValuePair<string, string>[_items.Length + 1];
        Array.Copy(_items, appended, _items.Length);
        appended[^1] = new KeyValuePair<string, string>(name, value);
        return new QueryParameters(appended);
    }

    /// <summary>
    /// 回傳已編碼的 query 字串,與 <see cref="ToQueryString"/> 相同。
    /// Returns the encoded query string, identical to <see cref="ToQueryString"/>.
    /// </summary>
    /// <returns>已編碼的 query 字串。The encoded query string.</returns>
    public override string ToString() => ToQueryString();
}

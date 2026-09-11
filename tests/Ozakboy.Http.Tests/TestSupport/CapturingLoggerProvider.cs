using Microsoft.Extensions.Logging;

namespace Ozakboy.Http.Tests.TestSupport;

/// <summary>
/// 攔下所有類別的日誌(含 <see cref="IHttpClientFactory"/> 自己的日誌與範圍),供外洩檢查使用。
/// Captures logging from every category — including <see cref="IHttpClientFactory"/>'s own entries and scopes —
/// for leak checks.
/// </summary>
/// <remarks>
/// 每一筆都保留四種會被輸出的形式:格式化訊息、結構化參數、範圍內容與例外文字。
/// 真實的日誌框架會輸出其中任何一種,外洩檢查必須四種都看。
/// Each entry keeps the four forms that end up emitted: the formatted message, the structured parameters, scope
/// content, and exception text. A real logging framework may emit any of them, so a leak check has to look at
/// all four.
/// </remarks>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<CapturedEntry> _entries = [];

    public IReadOnlyList<CapturedEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public string AllText => string.Join("\n", Entries.Select(entry => entry.AllText));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(CapturedEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    private static string Render(object? state) =>
        state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? string.Join(";", pairs.Select(pair => $"{pair.Key}={pair.Value}")) + "|" + state
            : state?.ToString() ?? string.Empty;

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            owner.Add(new CapturedEntry(category, LogLevel.None, "<scope>", Render(state), null));
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            owner.Add(new CapturedEntry(category, logLevel, formatter(state, exception), Render(state), exception));
        }
    }
}

internal sealed record CapturedEntry(string Category, LogLevel Level, string Message, string State, Exception? Exception)
{
    public string AllText => $"{Category}|{Level}|{Message}|{State}|{Exception}";
}

using Microsoft.Extensions.Logging;

namespace Ozakboy.Http.Tests.TestSupport;

/// <summary>
/// 把日誌訊息留在記憶體裡供斷言用的 <see cref="ILogger"/>。
/// An <see cref="ILogger"/> that keeps messages in memory for assertions.
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    public List<RecordedEntry> Entries { get; } = [];

    /// <summary>
    /// 所有訊息串成一段文字,方便檢查某個字串有沒有外洩。
    /// Every message joined into one blob, for checking whether some string leaked.
    /// </summary>
    public string AllText
    {
        get
        {
            lock (Entries)
            {
                return string.Join("\n", Entries.Select(entry => entry.Message));
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        lock (Entries)
        {
            Entries.Add(new RecordedEntry(logLevel, eventId.Id, formatter(state, exception), exception));
        }
    }

    internal sealed record RecordedEntry(LogLevel Level, int EventId, string Message, Exception? Exception);
}

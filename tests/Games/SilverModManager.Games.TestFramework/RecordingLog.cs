using Microsoft.Extensions.Logging;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// A logger provider that keeps every entry it is given, so a test can assert on what the code logged and at
/// which level.
/// </summary>
public sealed class RecordingLog : ILoggerProvider
{
    public sealed record Entry(LogLevel Level, string Category, string Message);

    private readonly List<Entry> _entries = [];

    /// <summary>A snapshot of what has been logged so far.</summary>
    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_entries) return _entries.ToArray();
        }
    }

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    /// <summary>An <see cref="ILogger{T}"/> that records into <see cref="Entries"/>.</summary>
    public ILogger<T> Logger<T>() => new RecordingLogger<T>(this);

    public void Dispose() { }

    private void Add(Entry entry)
    {
        lock (_entries) _entries.Add(entry);
    }

    private class RecordingLogger(RecordingLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Add(new Entry(logLevel, category, formatter(state, exception)));
        }
    }

    private sealed class RecordingLogger<T>(RecordingLog owner) : RecordingLogger(owner, typeof(T).FullName ?? typeof(T).Name), ILogger<T>;
}

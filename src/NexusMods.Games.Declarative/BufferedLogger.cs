using Microsoft.Extensions.Logging;

namespace NexusMods.Games.Declarative;

/// <summary>
/// Holds what the catalog logs while it loads. The catalog has to load while services are being
/// registered, because one game is registered per definition, and no logger exists yet at that point.
/// </summary>
internal sealed class BufferedLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> _entries = [];

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, eventId, formatter(state, exception), exception));
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>Writes everything buffered so far to <paramref name="target"/> and forgets it.</summary>
    public void ReplayTo(ILogger target)
    {
        (LogLevel Level, EventId EventId, string Message, Exception? Exception)[] entries;
        lock (_entries)
        {
            entries = [.._entries];
            _entries.Clear();
        }

        foreach (var entry in entries) target.Log(entry.Level, entry.EventId, entry.Message, entry.Exception, static (message, _) => message);
    }
}

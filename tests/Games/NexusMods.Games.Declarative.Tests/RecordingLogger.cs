using Microsoft.Extensions.Logging;

namespace NexusMods.Games.Declarative.Tests;

/// <summary>Keeps every formatted message with its level, for tests that assert what was reported.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IEnumerable<string> Messages(LogLevel level) => Entries.Where(entry => entry.Level == level).Select(entry => entry.Message);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}

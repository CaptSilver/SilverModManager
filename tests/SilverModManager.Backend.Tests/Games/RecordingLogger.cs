using Microsoft.Extensions.Logging;

namespace NexusMods.Backend.Tests.Games;

/// <summary>
/// Remembers the level of every message, for tests that assert a problem was reported. A substitute
/// can't stand in for <c>ILogger&lt;T&gt;</c> when T is internal.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogLevel> Levels { get; } = [];

    public int Count(LogLevel level) => Levels.Count(logged => logged == level);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Levels.Add(logLevel);

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}

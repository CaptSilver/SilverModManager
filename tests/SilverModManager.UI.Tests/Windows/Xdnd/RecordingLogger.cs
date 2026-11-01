using Microsoft.Extensions.Logging;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

public sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>The level a real logger's filter would cut at; <see cref="IsEnabled"/> follows it so guarded log calls can be checked.</summary>
    public LogLevel Minimum { get; set; } = LogLevel.Trace;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= Minimum;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

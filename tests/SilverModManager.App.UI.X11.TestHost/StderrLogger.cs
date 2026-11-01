using Microsoft.Extensions.Logging;

namespace SilverModManager.App.UI.X11.TestHost;

/// <summary>Writes every log line to stderr, which the test process keeps for its failure messages.</summary>
public sealed class StderrLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Console.Error.WriteLine($"{logLevel}: {typeof(T).Name}: {formatter(state, exception)}{(exception is null ? "" : " " + exception)}");
}

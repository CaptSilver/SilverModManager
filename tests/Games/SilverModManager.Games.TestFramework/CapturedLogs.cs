using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Collects every log line the test host writes, so a test can assert on what the log tells a person who reads it
/// after the fact. Added to the host's services; read back through <see cref="Lines"/>.
/// </summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLine> _lines = new();

    public IReadOnlyCollection<CapturedLine> Lines => _lines;

    /// <summary>Registers the collector and lets everything down to Trace through.</summary>
    public IServiceCollection AddTo(IServiceCollection services)
    {
        services.AddSingleton<ILoggerProvider>(this);
        services.Configure<LoggerFilterOptions>(options => options.MinLevel = LogLevel.Trace);
        return services;
    }

    public ILogger CreateLogger(string categoryName) => new Collector(categoryName, _lines);

    public void Dispose() { }

    private sealed class Collector(string category, ConcurrentQueue<CapturedLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => lines.Enqueue(new CapturedLine(logLevel, category, formatter(state, exception)));
    }
}

public sealed record CapturedLine(LogLevel Level, string Category, string Message);

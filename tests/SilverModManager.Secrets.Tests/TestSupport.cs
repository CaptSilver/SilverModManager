using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using R3;
using SilverModManager.Sdk;

namespace SilverModManager.Secrets.Tests;

internal sealed class FakeEnvironment(Dictionary<string, string>? values = null) : IEnvironmentReader
{
    public Dictionary<string, string> Values { get; } = values ?? new();

    public string? Get(string name) => Values.GetValueOrDefault(name);
}

internal sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
    }

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (Entries) return Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToArray();
        }
    }
}

internal abstract class DelegatingSecretStore(ISecretStore inner) : ISecretStore
{
    public virtual ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken cancellationToken) => inner.GetAsync(key, cancellationToken);

    public virtual ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken cancellationToken) => inner.SetAsync(key, value, cancellationToken);

    public virtual ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);

    public virtual ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);

    public virtual ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken cancellationToken) => inner.DescribeAsync(cancellationToken);

    public virtual ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken cancellationToken) => inner.UnlockAsync(cancellationToken);

    public virtual Observable<SecretKey> Changed => inner.Changed;

    public virtual Observable<SecretStoreStatus> StatusChanged => inner.StatusChanged;
}

/// <summary>Stands in for the Secret Service backend, which another project provides.</summary>
internal sealed class FakeServiceStore(InMemorySecretStore inner) : DelegatingSecretStore(inner), ISecretServiceStore;

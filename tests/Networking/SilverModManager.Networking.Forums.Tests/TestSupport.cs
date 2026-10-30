using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk;
using R3;

namespace SilverModManager.Networking.Forums.Tests;

/// <summary>
/// A store that behaves like <see cref="InMemorySecretStore"/> but never tells anyone about changes,
/// the way a write made by another process is invisible to this one.
/// </summary>
internal sealed class SilentSecretStore(InMemorySecretStore inner) : ISecretStore
{
    public Observable<SecretKey> Changed => Observable.Never<SecretKey>();

    public Observable<SecretStoreStatus> StatusChanged => Observable.Never<SecretStoreStatus>();

    public ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken cancellationToken) => inner.GetAsync(key, cancellationToken);

    public ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken cancellationToken) => inner.SetAsync(key, value, cancellationToken);

    public ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);

    public ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);

    public ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken cancellationToken) => inner.DescribeAsync(cancellationToken);

    public ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken cancellationToken) => inner.UnlockAsync(cancellationToken);
}

internal sealed class FakeEnvironment(params (string Name, string Value)[] variables) : IEnvironmentReader
{
    private readonly Dictionary<string, string> _variables = variables.ToDictionary(v => v.Name, v => v.Value);

    public string? Get(string name) => _variables.GetValueOrDefault(name);
}

using NexusMods.Abstractions.Secrets;
using NexusMods.Sdk;
using R3;

namespace NexusMods.Secrets.Overlay;

/// <summary>
/// A key whose value an override supplies, and which override that is (e.g. <c>environment variable NEXUS_API_KEY</c>).
/// </summary>
public sealed record SecretOverride(SecretKey Key, string Source);

/// <summary>
/// Read-only sources consulted before the real store: the process environment (the key's own
/// variable, then its alias) and a secrets file. A value found there is never written anywhere, and
/// setting or deleting such a key is refused instead of changing the store behind the override.
/// </summary>
public sealed class OverlaySecretStore : ISecretStore
{
    private readonly IEnvironmentReader _environment;
    private readonly Lazy<SecretsFileContents>? _file;
    private readonly string? _filePath;
    private readonly IReadOnlyDictionary<SecretKey, string> _aliases;
    private readonly IReadOnlyCollection<SecretKey> _knownKeys;

    /// <param name="inner">The store consulted when nothing overrides the key.</param>
    /// <param name="aliases">Key to the conventional environment variable that also supplies it, e.g. NEXUS_API_KEY.</param>
    /// <param name="knownKeys">Keys to look for overrides of when describing the store; the aliased keys are always included.</param>
    public OverlaySecretStore(
        ISecretStore inner,
        IEnvironmentReader environment,
        SecretsFileReader? file,
        IReadOnlyDictionary<SecretKey, string> aliases,
        IReadOnlyCollection<SecretKey>? knownKeys = null)
    {
        Inner = inner;
        _environment = environment;
        _filePath = file?.Path;
        // Read once: a headless process lives for one command or one game launch, and the file's mode warning belongs in the log once.
        _file = file is null ? null : new Lazy<SecretsFileContents>(file.Read);
        _aliases = aliases;
        _knownKeys = (knownKeys ?? []).Concat(aliases.Keys).Distinct().ToArray();
    }

    public ISecretStore Inner { get; }

    public Observable<SecretKey> Changed => Inner.Changed;

    public Observable<SecretStoreStatus> StatusChanged => Inner.StatusChanged;

    public async ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken cancellationToken)
    {
        var (value, _, error) = Resolve(key);
        if (value is not null) return new SecretResult.Found(value);
        if (error is not null) return new SecretResult.Unavailable(SecretUnavailableKind.Malformed, error.ToString());
        return await Inner.GetAsync(key, cancellationToken);
    }

    public async ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken cancellationToken)
    {
        if (RefusalFor(key) is { } refusal) return refusal;
        return await Inner.SetAsync(key, value, cancellationToken);
    }

    public async ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken)
    {
        if (RefusalFor(key) is { } refusal) return refusal;
        return await Inner.DeleteAsync(key, cancellationToken);
    }

    public ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken cancellationToken) => Inner.ListAsync(cancellationToken);

    public ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken cancellationToken) => Inner.UnlockAsync(cancellationToken);

    public async ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken cancellationToken)
    {
        var status = await Inner.DescribeAsync(cancellationToken);
        var overridden = _knownKeys.Where(key => Resolve(key).Value is not null).ToArray();
        return status with { OverriddenKeys = overridden.Length == 0 ? status.OverriddenKeys : status.OverriddenKeys.Concat(overridden).Distinct().ToArray() };
    }

    /// <summary>
    /// The known keys an override currently supplies, each with its source. Never carries a value.
    /// </summary>
    public IReadOnlyList<SecretOverride> Overrides()
        => _knownKeys
            .Select(key => Resolve(key).Source is { } source ? new SecretOverride(key, source) : null)
            .OfType<SecretOverride>()
            .ToArray();

    private SecretWriteResult? RefusalFor(SecretKey key)
    {
        var (_, source, error) = Resolve(key);
        if (source is not null) return new SecretWriteResult.ReadOnly(source);
        // Whether the key is pinned is unknown, and a write that the file then shadows would silently do nothing.
        if (error is not null) return new SecretWriteResult.Unavailable(SecretUnavailableKind.Malformed, error.ToString());
        return null;
    }

    private (string? Value, string? Source, SecretsFileError? Error) Resolve(SecretKey key)
    {
        var own = key.EnvironmentVariable;
        _aliases.TryGetValue(key, out var alias);

        if (FromEnvironment(own) is { } ownValue) return (ownValue, $"environment variable {own}", null);
        if (alias is not null && FromEnvironment(alias) is { } aliasValue) return (aliasValue, $"environment variable {alias}", null);

        if (_file is null) return (null, null, null);
        var contents = _file.Value;
        if (contents.Error is not null) return (null, null, contents.Error);

        var fromFile = Lookup(contents.Values, own) ?? (alias is null ? null : Lookup(contents.Values, alias));
        return fromFile is null ? (null, null, null) : (fromFile, $"--secrets-file {_filePath}", null);
    }

    // An empty variable is how a shell unsets something it cannot delete; it must not blank a stored credential.
    private string? FromEnvironment(string name) => _environment.Get(name) is { Length: > 0 } value ? value : null;

    private static string? Lookup(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
}

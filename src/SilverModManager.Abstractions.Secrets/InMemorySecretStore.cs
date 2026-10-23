using System.Collections.Concurrent;
using R3;

namespace SilverModManager.Abstractions.Secrets;

/// <summary>
/// A store that lives in the process. It is the fake for tests and the backend behind
/// <see cref="SecretBackendChoice.Memory"/>.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<SecretKey, string> _values = new();
    private readonly Subject<SecretKey> _changed = new();
    private readonly Subject<SecretStoreStatus> _statusChanged = new();
    private readonly List<string> _calls = [];
    private SecretStoreStatus _status = new(SecretBackendKind.Memory, "In-memory store", Locked: false, Unencrypted: false, Writable: true, []);

    /// <summary>
    /// What <see cref="DescribeAsync"/> and <see cref="UnlockAsync"/> return. Assigning it fires <see cref="StatusChanged"/>.
    /// </summary>
    public SecretStoreStatus Status
    {
        get => Volatile.Read(ref _status);
        set
        {
            Volatile.Write(ref _status, value);
            _statusChanged.OnNext(value);
        }
    }

    /// <summary>
    /// When set, Get, Set and Delete return Unavailable with this kind and reason and change nothing,
    /// and List is empty. Null means the store works.
    /// </summary>
    public (SecretUnavailableKind Kind, string Reason)? Availability { get; set; }

    /// <summary>
    /// Every call made so far, e.g. <c>Get nexus.api-key</c>. Values are never recorded.
    /// </summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls) return _calls.ToArray();
        }
    }

    public Observable<SecretKey> Changed => _changed;

    public Observable<SecretStoreStatus> StatusChanged => _statusChanged;

    public ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken cancellationToken)
    {
        Record($"Get {key}");
        if (Availability is { } down) return new(new SecretResult.Unavailable(down.Kind, down.Reason));
        return new(_values.TryGetValue(key, out var value) ? new SecretResult.Found(value) : new SecretResult.NotFound());
    }

    public ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken cancellationToken)
    {
        Record($"Set {key}");
        if (Availability is { } down) return new(new SecretWriteResult.Unavailable(down.Kind, down.Reason));
        _values[key] = value;
        _changed.OnNext(key);
        return new(new SecretWriteResult.Written());
    }

    public ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken)
    {
        Record($"Delete {key}");
        if (Availability is { } down) return new(new SecretWriteResult.Unavailable(down.Kind, down.Reason));
        if (_values.TryRemove(key, out _)) _changed.OnNext(key);
        return new(new SecretWriteResult.Written());
    }

    public ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken cancellationToken)
    {
        Record("List");
        IReadOnlyList<SecretKey> keys = Availability is null ? _values.Keys.ToArray() : [];
        return new(keys);
    }

    public ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken cancellationToken)
    {
        Record("Describe");
        return new(Status);
    }

    public ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken cancellationToken)
    {
        Record("Unlock");
        return new(Status);
    }

    private void Record(string call)
    {
        lock (_calls) _calls.Add(call);
    }
}

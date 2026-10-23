using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;
using R3;

namespace NexusMods.Networking.GOG;

/// <summary>
/// The GOG login as the client sees it. It owns the conversation with the secret store: it loads the
/// credential once, keeps it current when the store reports a change or an unlock, and writes through the store.
/// </summary>
/// <remarks>
/// Every accessor waits for the load, so a client built in a host that never ran the start-up priming still
/// sees what is stored.
/// </remarks>
public sealed class GogCredentialCache : ISecretPrimable, IDisposable
{
    private readonly ISecretStore _store;
    private readonly ILogger<GogCredentialCache> _logger;
    private readonly Lazy<Task> _loaded;
    private readonly object _lock = new();
    private readonly IDisposable _subscriptions;

    private GogCredential? _current;

    // True while the credential only lives here because the store would not take it.
    private bool _memoryOnly;

    public GogCredentialCache(ISecretStore store, ILogger<GogCredentialCache> logger)
    {
        _store = store;
        _logger = logger;
        _loaded = new Lazy<Task>(() => Task.Run(() => Guarded(ReadAsync, "load")));

        var changed = store.Changed
            .Where(static key => key == GogSecretKeys.OAuth)
            .SubscribeAwait(async (_, cancellationToken) =>
            {
                await Guarded(async () =>
                {
                    await EnsureLoadedAsync(cancellationToken);
                    await ReadAsync();
                }, "reload after a change");
            }, AwaitOperation.Sequential, configureAwait: false);

        var statusChanged = store.StatusChanged
            .Where(static status => !status.Locked)
            .SubscribeAwait(async (_, cancellationToken) =>
            {
                await Guarded(async () =>
                {
                    await EnsureLoadedAsync(cancellationToken);
                    // An unlocked store may now answer for a key it could not read before.
                    await ReadAsync();
                }, "reload after a status change");
            }, AwaitOperation.Sequential, configureAwait: false);

        _subscriptions = Disposable.Combine(changed, statusChanged);
    }

    /// <summary>
    /// The credential as of the last load or write; null before the load finishes.
    /// </summary>
    public GogCredential? Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    /// <summary>
    /// Loads the credential from the store, once. Later calls share that load.
    /// </summary>
    public Task EnsureLoadedAsync(CancellationToken cancellationToken) => _loaded.Value.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask PrimeAsync(CancellationToken cancellationToken) => await EnsureLoadedAsync(cancellationToken);

    public async ValueTask<GogCredential?> GetAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return Current;
    }

    /// <summary>
    /// Reads the credential from the store again and returns what the cache holds afterwards.
    /// </summary>
    public async ValueTask<GogCredential?> ReloadAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        await ReadAsync();
        return Current;
    }

    /// <summary>
    /// Stores the credential. When the store cannot take it the credential is still used for the life of the process.
    /// </summary>
    public async ValueTask<SecretWriteResult> SetAsync(GogCredential credential, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var result = await _store.SetAsync(GogSecretKeys.OAuth, credential.ToJson(), cancellationToken);
        switch (result)
        {
            case SecretWriteResult.Written:
                lock (_lock)
                {
                    _current = credential;
                    _memoryOnly = false;
                }

                break;
            case SecretWriteResult.Unavailable unavailable:
                lock (_lock)
                {
                    _current = credential;
                    _memoryOnly = true;
                }

                _logger.LogWarning("The secret store cannot take {Key} ({Kind}: {Reason}); the GOG login is kept in memory until the app closes", GogSecretKeys.OAuth, unavailable.Kind, unavailable.Reason);
                break;
        }

        // A ReadOnly result means something outside the store pins the value, so the cache keeps what it has.
        return result;
    }

    /// <summary>
    /// Removes the credential. One that comes from outside the store stays, and the result says so.
    /// </summary>
    public async ValueTask<SecretWriteResult> ClearAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var result = await _store.DeleteAsync(GogSecretKeys.OAuth, cancellationToken);
        if (result is not SecretWriteResult.ReadOnly)
        {
            lock (_lock)
            {
                _current = null;
                _memoryOnly = false;
            }
        }

        if (result is SecretWriteResult.Unavailable unavailable)
            _logger.LogWarning("Could not remove {Key} from the secret store ({Kind}: {Reason}); it was dropped for this session only", GogSecretKeys.OAuth, unavailable.Kind, unavailable.Reason);

        return result;
    }

    public void Dispose() => _subscriptions.Dispose();

    private async Task ReadAsync()
    {
        var result = await _store.GetAsync(GogSecretKeys.OAuth, CancellationToken.None);
        switch (result)
        {
            case SecretResult.Found found:
                if (GogCredential.TryParse(found.Value, out var credential))
                {
                    lock (_lock)
                    {
                        _current = credential;
                        _memoryOnly = false;
                    }
                }
                else
                {
                    _logger.LogWarning("The stored value for {Key} is not a GOG login; ignoring it", GogSecretKeys.OAuth);
                    lock (_lock) _current = null;
                }

                break;

            case SecretResult.NotFound:
                // A login that only lives in memory is not evicted by a store that has never heard of it.
                lock (_lock)
                {
                    if (!_memoryOnly) _current = null;
                }

                break;

            case SecretResult.Unavailable unavailable:
                _logger.LogDebug("Could not read {Key} from the secret store ({Kind}: {Reason})", GogSecretKeys.OAuth, unavailable.Kind, unavailable.Reason);
                break;
        }
    }

    private async Task Guarded(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A store reports trouble through Unavailable; anything thrown is a defect, and must not take the app down.
            _logger.LogError(e, "GOG credential cache {What} failed", what);
        }
    }
}

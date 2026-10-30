using Microsoft.Extensions.Logging;
using R3;
using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.Forums.Auth;

/// <summary>
/// Where a cached token came from.
/// </summary>
public enum ForumCredentialSource
{
    None,

    /// <summary>Read from the secret store.</summary>
    Stored,

    /// <summary>Supplied by the environment or a secrets file; cannot be changed from here.</summary>
    Override,

    /// <summary>The store would not take it, so it only lives as long as this process.</summary>
    Memory,
}

/// <summary>
/// One forum's token as the rest of the application sees it. It owns the conversation with the secret store:
/// it loads the token once, keeps it current when the store reports a change or an unlock, and writes through
/// the store.
/// </summary>
/// <remarks>
/// Every async accessor waits for the load, so a caller built in a host that never ran the start-up priming
/// still sees what is stored. <see cref="Current"/> does not wait; it is for callers that cannot await and
/// reads null until the load has finished.
/// </remarks>
public sealed class ForumCredentialCache : IForumTokenCache, ISecretPrimable, IDisposable
{
    private sealed record Held(OAuthToken Token, ForumCredentialSource Source, string? Pin);

    private readonly ForumSite _site;
    private readonly SecretKey _key;
    private readonly ISecretStore _store;
    private readonly ILogger _logger;
    private readonly Lazy<Task> _loaded;
    private readonly object _lock = new();
    private readonly object _publishLock = new();
    private readonly ReactiveProperty<bool> _hasCredential = new(false);
    private readonly Subject<Unit> _changes = new();
    private readonly IDisposable _subscriptions;

    private Held? _held;
    private SecretStoreStatus? _status;
    private int _loggedMemoryFallback;

    public ForumCredentialCache(ForumSite site, ISecretStore store, ILogger logger)
    {
        _site = site;
        _key = site.SecretKey();
        _store = store;
        _logger = logger;
        // On the thread pool: HasCredential subscribers run inside the load as soon as a value lands, and one that
        // awaits the load would otherwise re-enter the Lazy from its own factory.
        _loaded = new Lazy<Task>(() => Task.Run(LoadAsync));

        var changed = store.Changed
            .Where(key => key == _key)
            .SubscribeAwait(async (_, cancellationToken) =>
            {
                await Guarded(async () =>
                {
                    await EnsureLoadedAsync(cancellationToken);
                    await ReadAsync(cancellationToken);
                }, "reload after a change");
            }, AwaitOperation.Sequential, configureAwait: false);

        var statusChanged = store.StatusChanged
            .SubscribeAwait(async (status, cancellationToken) =>
            {
                await Guarded(async () =>
                {
                    await EnsureLoadedAsync(cancellationToken);
                    ApplyStatus(status);
                    // An unlocked store may now answer for a key it could not read before.
                    if (!status.Locked) await ReadAsync(cancellationToken);
                }, "reload after a status change");
            }, AwaitOperation.Sequential, configureAwait: false);

        _subscriptions = Disposable.Combine(changed, statusChanged);
    }

    /// <summary>
    /// True while a token is held. Replays the current value to a new subscriber.
    /// </summary>
    public ReadOnlyReactiveProperty<bool> HasCredential => _hasCredential;

    /// <summary>
    /// Fires after every write, removal or reload, including one that swaps a token for another. HasCredential
    /// cannot say that: an expired token replaced by a fresh one is true before and after.
    /// </summary>
    public Observable<Unit> Changes => _changes;

    /// <summary>
    /// The last known state of the store's ability to take a write. False until the first load.
    /// </summary>
    public bool Writable => _status?.Writable ?? false;

    /// <inheritdoc />
    public OAuthToken? Current
    {
        get
        {
            lock (_lock) return _held?.Token;
        }
    }

    public ForumCredentialSource TokenSource
    {
        get
        {
            lock (_lock) return _held?.Source ?? ForumCredentialSource.None;
        }
    }

    /// <summary>
    /// Where the token comes from when something outside the store pins it, e.g.
    /// <c>environment variable SILVERMM_SECRET_LOVERSLAB_OAUTH</c>; null when it can be changed.
    /// </summary>
    public string? PinnedSource
    {
        get
        {
            lock (_lock) return _held is { Source: ForumCredentialSource.Override } held ? held.Pin : null;
        }
    }

    /// <summary>
    /// Where the token lives, in words for the Settings page; empty without a token.
    /// </summary>
    public string Source
    {
        get
        {
            Held? held;
            lock (_lock) held = _held;
            return held?.Source switch
            {
                ForumCredentialSource.Override => held.Pin ?? "override",
                ForumCredentialSource.Memory => "memory (not stored)",
                ForumCredentialSource.Stored => _status?.Backend switch
                {
                    SecretBackendKind.SecretService => "Secret Service",
                    SecretBackendKind.File => "private file",
                    SecretBackendKind.Memory => "in-memory store",
                    _ => _status?.Detail ?? "secret store",
                },
                _ => string.Empty,
            };
        }
    }

    /// <summary>
    /// Loads the token from the store, once. Later calls share that load.
    /// </summary>
    public Task EnsureLoadedAsync(CancellationToken cancellationToken) => _loaded.Value.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask PrimeAsync(CancellationToken cancellationToken) => await EnsureLoadedAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask<OAuthToken?> GetTokenAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return Current;
    }

    /// <inheritdoc />
    public async ValueTask<OAuthToken?> ReloadTokenAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        await ReadAsync(cancellationToken);
        return Current;
    }

    /// <inheritdoc />
    public async ValueTask<SecretWriteResult> SetTokenAsync(OAuthToken token, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var result = await _store.SetAsync(_key, token.ToJson(), cancellationToken);
        switch (result)
        {
            case SecretWriteResult.Written:
                lock (_lock) _held = new Held(token, ForumCredentialSource.Stored, null);
                break;
            case SecretWriteResult.Unavailable unavailable:
                lock (_lock) _held = new Held(token, ForumCredentialSource.Memory, null);
                if (Interlocked.Exchange(ref _loggedMemoryFallback, 1) == 0)
                    _logger.LogWarning("The secret store cannot take {Key} ({Kind}: {Reason}); the {Site} sign-in is kept in memory until the app closes", _key, unavailable.Kind, unavailable.Reason, _site);
                break;
            default:
                // Pinned from outside: the value in use does not change.
                break;
        }

        Publish();
        return result;
    }

    /// <summary>
    /// Removes the token. One that comes from outside the store stays, and the result says so.
    /// </summary>
    public async ValueTask<SecretWriteResult> ClearAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var result = await _store.DeleteAsync(_key, cancellationToken);
        if (result is not SecretWriteResult.ReadOnly)
        {
            lock (_lock) _held = null;
        }

        if (result is SecretWriteResult.Unavailable unavailable)
            _logger.LogWarning("Could not remove {Key} from the secret store ({Kind}: {Reason}); it was dropped for this session only", _key, unavailable.Kind, unavailable.Reason);

        Publish();
        return result;
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
        _hasCredential.Dispose();
        _changes.Dispose();
    }

    private void Publish()
    {
        lock (_publishLock)
        {
            bool has;
            lock (_lock) has = _held is not null;
            _hasCredential.Value = has;
            _changes.OnNext(Unit.Default);
        }
    }

    private void ApplyStatus(SecretStoreStatus status)
    {
        // StatusChanged comes from the layer under the overlay, so it does not name the keys the overlay pins.
        var overridden = _status is null ? status.OverriddenKeys : status.OverriddenKeys.Concat(_status.OverriddenKeys).Distinct().ToArray();
        _status = status with { OverriddenKeys = overridden };
    }

    private async Task LoadAsync()
    {
        await Guarded(async () =>
        {
            // Describe first: Writable must have a value before any StatusChanged arrives.
            ApplyStatus(await _store.DescribeAsync(CancellationToken.None));
            await ReadAsync(CancellationToken.None);
        }, "load");
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var result = await _store.GetAsync(_key, cancellationToken);
        switch (result)
        {
            case SecretResult.Found found:
                if (OAuthToken.TryParse(found.Value, out var token))
                {
                    var pin = _status is { } status && status.OverriddenKeys.Contains(_key)
                        ? await FindPinAsync(found.Value, cancellationToken)
                        : null;
                    var source = pin is null ? ForumCredentialSource.Stored : ForumCredentialSource.Override;
                    lock (_lock) _held = new Held(token, source, pin);
                }
                else
                {
                    _logger.LogWarning("The stored value for {Key} is not a token; ignoring it", _key);
                    lock (_lock) _held = null;
                }

                break;

            case SecretResult.NotFound:
                lock (_lock)
                {
                    // A token that only lives in memory is not evicted by a store that has never heard of it.
                    if (_held is not { Source: ForumCredentialSource.Memory }) _held = null;
                }

                break;

            case SecretResult.Unavailable unavailable:
                _logger.LogDebug("Could not read {Key} from the secret store ({Kind}: {Reason})", _key, unavailable.Kind, unavailable.Reason);
                break;
        }

        Publish();
    }

    /// <summary>
    /// Asks the store who pins the key. The overlay refuses a write to a pinned key and names the source in
    /// the refusal, and that is the only place the source is told.
    /// </summary>
    private async Task<string?> FindPinAsync(string value, CancellationToken cancellationToken)
    {
        var probe = await _store.SetAsync(_key, value, cancellationToken);
        if (probe is SecretWriteResult.ReadOnly readOnly) return readOnly.Source;

        _logger.LogWarning("The secret store lists {Key} as overridden but accepted a write to it", _key);
        return "an override";
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
            _logger.LogError(e, "Forum credential cache {What} for {Site} failed", what, _site);
        }
    }
}

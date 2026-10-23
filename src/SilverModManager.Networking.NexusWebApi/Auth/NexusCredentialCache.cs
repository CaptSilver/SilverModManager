using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.NexusWebApi.DTOs.OAuth;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk.NexusModsApi;
using R3;

namespace SilverModManager.Networking.NexusWebApi.Auth;

/// <summary>
/// Where a cached credential came from.
/// </summary>
public enum CredentialSource
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
/// The Nexus credentials as the rest of the application sees them. It owns the conversation with the
/// secret store: it loads both keys once, keeps them current when the store reports a change or an unlock,
/// and writes through the store.
/// </summary>
/// <remarks>
/// Every accessor waits for the load, so a message factory, verb or login manager built in any order, in a
/// host that never ran the start-up priming, still sees what is stored.
/// </remarks>
public sealed class NexusCredentialCache : ISecretPrimable, INexusCredentialSummary, IDisposable
{
    private sealed record Held<T>(T Value, CredentialSource Source, string? Pin);

    private readonly ISecretStore _store;
    private readonly ILogger<NexusCredentialCache> _logger;
    private readonly Lazy<Task> _loaded;
    private readonly object _lock = new();
    private readonly object _publishLock = new();
    private readonly ReactiveProperty<bool> _hasCredential = new(false);
    private readonly IDisposable _subscriptions;

    private Held<string>? _apiKey;
    private Held<NexusOAuthToken>? _token;
    private SecretStoreStatus? _status;
    private int _loggedMemoryFallback;

    public NexusCredentialCache(ISecretStore store, ILogger<NexusCredentialCache> logger)
    {
        _store = store;
        _logger = logger;
        // On the thread pool: HasCredential subscribers run inside the load as soon as a value lands, and one that
        // awaits the load would otherwise re-enter the Lazy from its own factory.
        _loaded = new Lazy<Task>(() => Task.Run(LoadAsync));

        var changed = store.Changed
            .Where(static key => key == NexusSecretKeys.ApiKey || key == NexusSecretKeys.OAuth)
            .SubscribeAwait(async (key, cancellationToken) =>
            {
                await Guarded(async () =>
                {
                    await EnsureLoadedAsync(cancellationToken);
                    await ReadAsync(key, cancellationToken);
                }, "reload after a change");
            }, AwaitOperation.Sequential, configureAwait: false);

        var statusChanged = store.StatusChanged
            .SubscribeAwait(async (status, cancellationToken) =>
            {
                await Guarded(async () =>
                {
                    await EnsureLoadedAsync(cancellationToken);
                    ApplyStatus(status);
                    // An unlocked store may now answer for keys it could not read before.
                    if (!status.Locked) await ReadAllAsync(cancellationToken);
                }, "reload after a status change");
            }, AwaitOperation.Sequential, configureAwait: false);

        _subscriptions = Disposable.Combine(changed, statusChanged);
    }

    /// <summary>
    /// True while a key or an OAuth token is held. Replays the current value to a new subscriber.
    /// </summary>
    public ReadOnlyReactiveProperty<bool> HasCredential => _hasCredential;

    /// <summary>
    /// The last known state of the store's ability to take a write. False until the first load.
    /// </summary>
    public bool Writable => _status?.Writable ?? false;

    public CredentialSource ApiKeySource => _apiKey?.Source ?? CredentialSource.None;

    public CredentialSource TokenSource => _token?.Source ?? CredentialSource.None;

    /// <summary>
    /// Where the API key comes from when something outside the store pins it, e.g.
    /// <c>environment variable NEXUS_API_KEY</c>; null when the key can be changed.
    /// </summary>
    public string? PinnedApiKeySource => _apiKey is { Source: CredentialSource.Override } held ? held.Pin : null;

    /// <inheritdoc />
    public NexusCredentialKind Kind => _apiKey is not null ? NexusCredentialKind.ApiKey
        : _token is not null ? NexusCredentialKind.OAuthToken
        : NexusCredentialKind.None;

    /// <inheritdoc />
    public string Source
    {
        get
        {
            var (source, pin) = _apiKey is { } key ? (key.Source, key.Pin)
                : _token is { } token ? (token.Source, token.Pin)
                : (CredentialSource.None, null);

            return source switch
            {
                CredentialSource.Override => pin ?? "override",
                CredentialSource.Memory => "memory (not stored)",
                CredentialSource.Stored => _status?.Backend switch
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
    /// Loads the credentials from the store, once. Later calls share that load.
    /// </summary>
    public Task EnsureLoadedAsync(CancellationToken cancellationToken) => _loaded.Value.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask PrimeAsync(CancellationToken cancellationToken) => await EnsureLoadedAsync(cancellationToken);

    public async ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _apiKey?.Value;
    }

    public async ValueTask<NexusOAuthToken?> GetTokenAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _token?.Value;
    }

    /// <summary>
    /// Reads the token from the store again and returns what the cache holds afterwards. Another process may
    /// have refreshed it since this one looked.
    /// </summary>
    public async ValueTask<NexusOAuthToken?> ReloadTokenAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        await ReadAsync(NexusSecretKeys.OAuth, cancellationToken);
        return _token?.Value;
    }

    /// <summary>
    /// Stores the API key. When the store cannot take it the key is still used for the life of the process.
    /// </summary>
    public async ValueTask<SecretWriteResult> SetApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var result = await _store.SetAsync(NexusSecretKeys.ApiKey, apiKey, cancellationToken);
        Apply(result, NexusSecretKeys.ApiKey, () => _apiKey = MakeHeld(apiKey, result));
        return result;
    }

    /// <summary>
    /// Stores the token a sign-in or refresh returned.
    /// </summary>
    /// <exception cref="ArgumentException">The reply lacks the access or refresh token.</exception>
    public async ValueTask<SecretWriteResult> SetTokenAsync(JwtTokenReply reply, CancellationToken cancellationToken)
    {
        var token = NexusOAuthToken.FromReply(reply) ?? throw new ArgumentException("The reply carries no access or refresh token.", nameof(reply));
        return await SetTokenAsync(token, cancellationToken);
    }

    /// <inheritdoc cref="SetTokenAsync(JwtTokenReply, CancellationToken)"/>
    public async ValueTask<SecretWriteResult> SetTokenAsync(NexusOAuthToken token, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var result = await _store.SetAsync(NexusSecretKeys.OAuth, token.ToJson(), cancellationToken);
        Apply(result, NexusSecretKeys.OAuth, () => _token = MakeHeld(token, result));
        return result;
    }

    /// <summary>
    /// Removes the API key and the token. A credential that comes from outside the store stays, and the result says so.
    /// </summary>
    public async ValueTask<SecretWriteResult> ClearAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);

        SecretWriteResult? first = null;
        foreach (var key in NexusSecretKeys.All)
        {
            var result = await _store.DeleteAsync(key, cancellationToken);
            if (result is not SecretWriteResult.ReadOnly)
            {
                lock (_lock)
                {
                    if (key == NexusSecretKeys.ApiKey) _apiKey = null;
                    else _token = null;
                }
            }

            if (result is SecretWriteResult.Unavailable unavailable)
                _logger.LogWarning("Could not remove {Key} from the secret store ({Kind}: {Reason}); it was dropped for this session only", key, unavailable.Kind, unavailable.Reason);

            if (result is not SecretWriteResult.Written) first ??= result;
        }

        Publish();
        return first ?? new SecretWriteResult.Written();
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
        _hasCredential.Dispose();
    }

    private static Held<T> MakeHeld<T>(T value, SecretWriteResult result) => result is SecretWriteResult.Written
        ? new Held<T>(value, CredentialSource.Stored, null)
        : new Held<T>(value, CredentialSource.Memory, null);

    private void Apply(SecretWriteResult result, SecretKey key, Action keep)
    {
        switch (result)
        {
            case SecretWriteResult.Written:
                lock (_lock) keep();
                break;
            case SecretWriteResult.Unavailable unavailable:
                lock (_lock) keep();
                if (Interlocked.Exchange(ref _loggedMemoryFallback, 1) == 0)
                    _logger.LogWarning("The secret store cannot take {Key} ({Kind}: {Reason}); it is kept in memory until the app closes", key, unavailable.Kind, unavailable.Reason);
                break;
            default:
                // Pinned from outside: the value in use does not change.
                break;
        }

        Publish();
    }

    private void Publish()
    {
        lock (_publishLock)
        {
            bool has;
            lock (_lock) has = _apiKey is not null || _token is not null;
            _hasCredential.Value = has;
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
            await ReadAllAsync(CancellationToken.None);
        }, "load");
    }

    private async Task ReadAllAsync(CancellationToken cancellationToken)
    {
        foreach (var key in NexusSecretKeys.All) await ReadAsync(key, cancellationToken);
    }

    private async Task ReadAsync(SecretKey key, CancellationToken cancellationToken)
    {
        var result = await _store.GetAsync(key, cancellationToken);
        switch (result)
        {
            case SecretResult.Found found:
                var pin = _status is { } status && status.OverriddenKeys.Contains(key)
                    ? await FindPinAsync(key, found.Value, cancellationToken)
                    : null;
                var source = pin is null ? CredentialSource.Stored : CredentialSource.Override;
                lock (_lock)
                {
                    if (key == NexusSecretKeys.ApiKey) _apiKey = new Held<string>(found.Value, source, pin);
                    else if (NexusOAuthToken.TryParse(found.Value, out var token)) _token = new Held<NexusOAuthToken>(token, source, pin);
                    else
                    {
                        _logger.LogWarning("The stored value for {Key} is not a token; ignoring it", key);
                        _token = null;
                    }
                }

                break;

            case SecretResult.NotFound:
                lock (_lock)
                {
                    // A value that only lives in memory is not evicted by a store that has never heard of it.
                    if (key == NexusSecretKeys.ApiKey && _apiKey is not { Source: CredentialSource.Memory }) _apiKey = null;
                    else if (key == NexusSecretKeys.OAuth && _token is not { Source: CredentialSource.Memory }) _token = null;
                }

                break;

            case SecretResult.Unavailable unavailable:
                _logger.LogDebug("Could not read {Key} from the secret store ({Kind}: {Reason})", key, unavailable.Kind, unavailable.Reason);
                break;
        }

        Publish();
    }

    /// <summary>
    /// Asks the store who pins the key. The overlay refuses a write to a pinned key and names the source in
    /// the refusal, and that is the only place the source is told.
    /// </summary>
    private async Task<string?> FindPinAsync(SecretKey key, string value, CancellationToken cancellationToken)
    {
        var probe = await _store.SetAsync(key, value, cancellationToken);
        if (probe is SecretWriteResult.ReadOnly readOnly) return readOnly.Source;

        _logger.LogWarning("The secret store lists {Key} as overridden but accepted a write to it", key);
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
            _logger.LogError(e, "Nexus credential cache {What} failed", what);
        }
    }
}

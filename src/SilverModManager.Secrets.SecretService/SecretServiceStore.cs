using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk;
using R3;

namespace SilverModManager.Secrets.SecretService;

/// <summary>
/// Keeps secrets as items in the user's default Secret Service collection (KWallet's ksecretd, GNOME Keyring).
/// </summary>
/// <remarks>
/// Every call turns trouble into <see cref="SecretResult.Unavailable"/> rather than throwing. Prompts are only
/// ever run from <see cref="UnlockAsync"/>: a prompt path handed back by any other call means the keyring is
/// locked, and answering it from a background write would pop a dialog the user never asked for.
/// </remarks>
internal sealed class SecretServiceStore : ISecretServiceStore, IDisposable
{
    public const string Schema = "io.github.CaptSilver.SilverModManager.Secret";
    public const string Application = "SilverModManager";
    public const string BusAddressVariable = "DBUS_SESSION_BUS_ADDRESS";

    /// <summary>
    /// With no keyring daemon running, starting kwalletd6 is what brings up ksecretd and with it
    /// <c>org.freedesktop.secrets</c>. GNOME Keyring needs no help: the bus activates it on the first call.
    /// </summary>
    public const string ActivationName = "org.kde.kwalletd6";

    private readonly ISecretServiceConnection _connection;
    private readonly IEnvironmentReader _environment;
    private readonly SecretStoreOptions _options;
    private readonly ILogger _logger;
    private readonly SecretServiceTimeouts _timeouts;

    private readonly Subject<SecretKey> _changed = new();
    private readonly Subject<SecretStoreStatus> _statusChanged = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private bool _connected;
    private string? _watchedCollection;
    private IDisposable? _lockedSubscription;

    public SecretServiceStore(
        ISecretServiceConnection connection,
        IEnvironmentReader environment,
        SecretStoreOptions options,
        ILogger logger,
        SecretServiceTimeouts? timeouts = null)
    {
        _connection = connection;
        _environment = environment;
        _options = options;
        _logger = logger;
        _timeouts = timeouts ?? SecretServiceTimeouts.Default;
    }

    public Observable<SecretKey> Changed => _changed;

    public Observable<SecretStoreStatus> StatusChanged => _statusChanged;

    private TimeSpan CallTimeout => _options.Mode == SecretStoreMode.Interactive ? _timeouts.Interactive : _timeouts.NonInteractive;

    public ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken cancellationToken) => RunAsync<SecretResult>(
        nameof(GetAsync), CallTimeout,
        async (_, ct) =>
        {
            var hits = await _connection.SearchAsync(ItemAttributes(key), ct);
            if (hits.Unlocked.Count > 0)
                return new SecretResult.Found(await _connection.GetSecretAsync(hits.Unlocked[0], ct));
            if (hits.Locked.Count > 0) throw Locked("the item is in a locked collection");
            return new SecretResult.NotFound();
        },
        (kind, reason) => new SecretResult.Unavailable(kind, reason),
        cancellationToken);

    public async ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken cancellationToken)
    {
        var result = await RunAsync<SecretWriteResult>(
            nameof(SetAsync), CallTimeout,
            async (collection, ct) =>
            {
                var created = await _connection.CreateItemAsync(
                    collection, $"{Application}: {key.Name}", ItemAttributes(key), value, replace: true, ct);
                if (created.Prompt is not null) throw Locked("the keyring asked for an unlock prompt");
                if (created.Item is null) throw new UnavailableException(SecretUnavailableKind.Malformed, "CreateItem returned neither an item nor a prompt");
                return new SecretWriteResult.Written();
            },
            (kind, reason) => new SecretWriteResult.Unavailable(kind, reason),
            cancellationToken);

        if (result is SecretWriteResult.Written) _changed.OnNext(key);
        return result;
    }

    public async ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken)
    {
        var result = await RunAsync<SecretWriteResult>(
            nameof(DeleteAsync), CallTimeout,
            async (_, ct) =>
            {
                var hits = await _connection.SearchAsync(ItemAttributes(key), ct);
                if (hits.Unlocked.Count == 0 && hits.Locked.Count > 0) throw Locked("the item is in a locked collection");

                foreach (var item in hits.Unlocked)
                {
                    if (await _connection.DeleteAsync(item, ct) is not null)
                        throw Locked("the keyring asked for an unlock prompt");
                }
                return new SecretWriteResult.Written();
            },
            (kind, reason) => new SecretWriteResult.Unavailable(kind, reason),
            cancellationToken);

        if (result is SecretWriteResult.Written) _changed.OnNext(key);
        return result;
    }

    public ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken cancellationToken) => RunAsync<IReadOnlyList<SecretKey>>(
        nameof(ListAsync), CallTimeout,
        async (_, ct) =>
        {
            // Item attributes stay readable while the collection is locked, so locked hits are listed too.
            var hits = await _connection.SearchAsync(BaseAttributes(), ct);
            var keys = new List<SecretKey>();
            foreach (var item in hits.Unlocked.Concat(hits.Locked))
            {
                var attributes = await _connection.GetAttributesAsync(item, ct);
                if (!attributes.TryGetValue("key", out var name)) continue;
                try
                {
                    keys.Add(new SecretKey(name));
                }
                catch (ArgumentException)
                {
                    // Something else wrote an item under our schema with a name we would never have produced.
                    _logger.LogWarning("Ignoring a Secret Service item whose key attribute is not a valid secret key");
                }
            }
            return keys;
        },
        (_, _) => [],
        cancellationToken);

    public ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken cancellationToken) => RunAsync(
        nameof(DescribeAsync), CallTimeout,
        async (collection, ct) => StatusFor(collection, await _connection.IsLockedAsync(collection, ct)),
        (kind, reason) => UnavailableStatus(kind, reason, locked: kind == SecretUnavailableKind.Locked),
        cancellationToken);

    public ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken cancellationToken)
    {
        if (_options.Mode != SecretStoreMode.Interactive) return DescribeAsync(cancellationToken);

        return RunAsync(
            nameof(UnlockAsync), _timeouts.Unlock,
            async (collection, ct) =>
            {
                if (!await _connection.IsLockedAsync(collection, ct)) return StatusFor(collection, locked: false);

                var unlock = await _connection.UnlockAsync([collection], ct);
                if (unlock.Prompt is not null && await _connection.PromptAsync(unlock.Prompt, ct))
                    return UnavailableStatus(SecretUnavailableKind.Dismissed, "the unlock prompt was dismissed", locked: true) with { Detail = $"{collection} (unlock prompt dismissed)" };

                // The Locked signal is not reliable on every daemon, so ask again rather than trust the prompt's outcome.
                var status = StatusFor(collection, await _connection.IsLockedAsync(collection, ct));
                if (!status.Locked) _statusChanged.OnNext(status);
                return status;
            },
            // Whatever stopped the unlock, the collection is still the locked one we were asked to open.
            (kind, reason) => UnavailableStatus(kind, reason, locked: kind is SecretUnavailableKind.Timeout or SecretUnavailableKind.Locked or SecretUnavailableKind.Dismissed),
            cancellationToken);
    }

    public void Dispose()
    {
        _lockedSubscription?.Dispose();
        (_connection as IDisposable)?.Dispose();
        _changed.Dispose();
        _statusChanged.Dispose();
        _stateGate.Dispose();
    }

    private static Dictionary<string, string> BaseAttributes() => new()
    {
        ["xdg:schema"] = Schema,
        ["application"] = Application,
    };

    private static Dictionary<string, string> ItemAttributes(SecretKey key)
    {
        var attributes = BaseAttributes();
        attributes["key"] = key.Name;
        return attributes;
    }

    private static SecretStoreStatus StatusFor(string collection, bool locked) =>
        new(SecretBackendKind.SecretService, collection, Locked: locked, Unencrypted: false, Writable: !locked, OverriddenKeys: []);

    private static SecretStoreStatus UnavailableStatus(SecretUnavailableKind kind, string reason, bool locked) =>
        new(SecretBackendKind.SecretService, $"unavailable ({kind}): {reason}", Locked: locked, Unencrypted: false, Writable: false, OverriddenKeys: []);

    private static UnavailableException Locked(string reason) => new(SecretUnavailableKind.Locked, reason);

    /// <summary>
    /// Runs one operation against the default collection: connects on first use, applies the per-call timeout,
    /// starts the service once if nobody owns the name, and turns every failure into <paramref name="unavailable"/>.
    /// </summary>
    private async ValueTask<T> RunAsync<T>(
        string operation,
        TimeSpan limit,
        Func<string, CancellationToken, Task<T>> body,
        Func<SecretUnavailableKind, string, T> unavailable,
        CancellationToken cancellationToken)
    {
        var address = _environment.Get(BusAddressVariable);
        if (string.IsNullOrEmpty(address))
            return unavailable(SecretUnavailableKind.NoBus, $"{BusAddressVariable} is not set");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);

        try
        {
            return await WithServiceStartedAsync(address, body, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return unavailable(SecretUnavailableKind.Timeout, $"{operation} took longer than {limit.TotalSeconds:0.#} s");
        }
        catch (UnavailableException e)
        {
            return unavailable(e.Kind, e.Message);
        }
        catch (SecretServiceException e)
        {
            return await OnServiceExceptionAsync(e, operation, unavailable);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Secret Service {Operation} failed", operation);
            return unavailable(SecretUnavailableKind.Faulted, e.Message);
        }
    }

    private async Task<T> OnServiceExceptionAsync<T>(SecretServiceException e, string operation, Func<SecretUnavailableKind, string, T> unavailable)
    {
        switch (e.Failure)
        {
            case SecretServiceFailure.ServiceUnknown:
                return unavailable(SecretUnavailableKind.ServiceAbsent, "no Secret Service is running");
            case SecretServiceFailure.Locked:
                return unavailable(SecretUnavailableKind.Locked, e.Message);
            case SecretServiceFailure.Disconnected:
                await ResetConnectionAsync();
                break;
        }

        _logger.LogWarning(e, "Secret Service {Operation} failed ({Failure})", operation, e.Failure);
        return unavailable(SecretUnavailableKind.Faulted, e.Message);
    }

    private async Task<T> WithServiceStartedAsync<T>(string address, Func<string, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        try
        {
            return await AttemptAsync(address, body, ct);
        }
        catch (SecretServiceException e) when (e.Failure == SecretServiceFailure.ServiceUnknown)
        {
            _logger.LogDebug("Nobody owns org.freedesktop.secrets; starting {Activation}", ActivationName);
        }

        try
        {
            await _connection.StartServiceByNameAsync(ActivationName, ct);
        }
        catch (SecretServiceException e)
        {
            throw new SecretServiceException(SecretServiceFailure.ServiceUnknown, $"{ActivationName} could not be started: {e.Message}", e);
        }

        return await AttemptAsync(address, body, ct);
    }

    private async Task<T> AttemptAsync<T>(string address, Func<string, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        await EnsureConnectedAsync(address, ct);

        var collection = await _connection.ReadDefaultCollectionAsync(ct)
            ?? throw new UnavailableException(SecretUnavailableKind.NoDefaultCollection, "the Secret Service has no default collection");

        await EnsureWatchingAsync(collection, ct);
        return await body(collection, ct);
    }

    private async Task EnsureConnectedAsync(string address, CancellationToken ct)
    {
        await _stateGate.WaitAsync(ct);
        try
        {
            if (_connected) return;
            try
            {
                await _connection.ConnectAsync(address, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A bus we cannot reach is the same as no bus: the fallback chain only moves writes to the
                // file for NoBus, and a stale address left over from an ended session is common.
                throw new UnavailableException(SecretUnavailableKind.NoBus, $"cannot reach the bus at {address}: {e.Message}");
            }
            _connected = true;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task EnsureWatchingAsync(string collection, CancellationToken ct)
    {
        await _stateGate.WaitAsync(ct);
        try
        {
            if (_watchedCollection == collection) return;
            _lockedSubscription?.Dispose();
            _lockedSubscription = _connection.LockedChanged(collection).Subscribe(locked => _statusChanged.OnNext(StatusFor(collection, locked)));
            _watchedCollection = collection;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>
    /// After the bus connection closed the next call has to connect again, and the locked-state watch died with it.
    /// </summary>
    private async Task ResetConnectionAsync()
    {
        await _stateGate.WaitAsync();
        try
        {
            _connected = false;
            _lockedSubscription?.Dispose();
            _lockedSubscription = null;
            _watchedCollection = null;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>
    /// A condition the store already knows how to report, thrown from inside an operation body.
    /// </summary>
    private sealed class UnavailableException(SecretUnavailableKind kind, string message) : Exception(message)
    {
        public SecretUnavailableKind Kind { get; } = kind;
    }
}

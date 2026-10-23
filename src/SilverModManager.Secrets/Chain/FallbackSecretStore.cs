using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;
using R3;

namespace NexusMods.Secrets.Chain;

/// <summary>
/// The Secret Service first, the owner-only file second.
/// </summary>
/// <remarks>
/// The file only gains content while no keyring answers, so when both hold a key the file's copy is
/// the newer one. A read that finds the keyring reachable therefore moves the file's value into it;
/// a write that reaches the keyring replaces the file's entry. A keyring that appears later absorbs
/// the file, never the other way round.
///
/// A write goes to the file only when there is no keyring at all (<see cref="SecretUnavailableKind.NoBus"/>,
/// <see cref="SecretUnavailableKind.ServiceAbsent"/>). A keyring that is locked, slow or has no default
/// collection exists and will come back, and a second copy in a file would diverge from it.
/// </remarks>
public sealed class FallbackSecretStore : ISecretStore
{
    private readonly ISecretStore? _service;
    private readonly ISecretStore _file;
    private readonly SecretStoreMode _mode;
    private readonly ILogger _logger;
    private readonly Observable<SecretKey> _changed;
    private readonly Observable<SecretStoreStatus> _statusChanged;
    private int _warnedAboutFile;
    private volatile bool _serviceAbsent;

    public FallbackSecretStore(ISecretStore? service, ISecretStore file, SecretStoreMode mode, ILogger logger)
    {
        _service = service;
        _file = file;
        _mode = mode;
        _logger = logger;
        _changed = service is null ? file.Changed : Observable.Merge(service.Changed, file.Changed);
        _statusChanged = service is null ? file.StatusChanged : Observable.Merge(service.StatusChanged, file.StatusChanged);
    }

    public Observable<SecretKey> Changed => _changed;

    public Observable<SecretStoreStatus> StatusChanged => _statusChanged;

    public async ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken cancellationToken)
    {
        if (_service is null) return await _file.GetAsync(key, cancellationToken);

        var fromService = await _service.GetAsync(key, cancellationToken);
        switch (fromService)
        {
            case SecretResult.Unavailable unavailable:
            {
                NoteServiceAnswer(unavailable.Kind);
                var fromFile = await _file.GetAsync(key, cancellationToken);
                // The keyring may hold the key; a file miss does not mean it is absent.
                return fromFile is SecretResult.Found ? fromFile : unavailable;
            }
            default:
            {
                NoteServiceAnswer(null);
                if (await _file.GetAsync(key, cancellationToken) is not SecretResult.Found copy) return fromService;
                await MoveFileCopyIntoServiceAsync(_service, key, copy.Value, cancellationToken);
                return copy;
            }
        }
    }

    public async ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken cancellationToken)
    {
        if (_service is null) return await WriteToFileAsync(key, value, cancellationToken);

        var result = await _service.SetAsync(key, value, cancellationToken);
        if (result is SecretWriteResult.Unavailable unavailable)
        {
            NoteServiceAnswer(unavailable.Kind);
            return unavailable.Kind is SecretUnavailableKind.NoBus or SecretUnavailableKind.ServiceAbsent
                ? await WriteToFileAsync(key, value, cancellationToken)
                : result;
        }

        NoteServiceAnswer(null);
        if (result is SecretWriteResult.Written) await RemoveFileCopyAsync(key, cancellationToken);
        return result;
    }

    public async ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken)
    {
        // The file copy goes either way: a logout must not leave a token behind in plain text.
        var fromFile = await _file.DeleteAsync(key, cancellationToken);
        if (_service is null) return fromFile;

        var fromService = await _service.DeleteAsync(key, cancellationToken);
        if (fromService is SecretWriteResult.Unavailable unavailable)
        {
            NoteServiceAnswer(unavailable.Kind);
            return unavailable.Kind is SecretUnavailableKind.NoBus or SecretUnavailableKind.ServiceAbsent ? fromFile : fromService;
        }

        NoteServiceAnswer(null);
        return fromFile is SecretWriteResult.Unavailable ? fromFile : fromService;
    }

    public async ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken cancellationToken)
    {
        var fromFile = await _file.ListAsync(cancellationToken);
        if (_service is null) return fromFile;
        return (await _service.ListAsync(cancellationToken)).Concat(fromFile).Distinct().ToArray();
    }

    public async ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken cancellationToken)
    {
        // The interface promises a prompt only in interactive mode; do not rely on every backend to check.
        if (_service is not null && _mode == SecretStoreMode.Interactive) await _service.UnlockAsync(cancellationToken);
        return await DescribeAsync(cancellationToken);
    }

    public async ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken cancellationToken)
    {
        var file = await _file.DescribeAsync(cancellationToken);
        if (_service is null) return file with { Unencrypted = true };

        var service = await _service.DescribeAsync(cancellationToken);
        var fileHoldsSecrets = (await _file.ListAsync(cancellationToken)).Count > 0;
        var onFile = fileHoldsSecrets || _serviceAbsent;
        return new SecretStoreStatus(
            Backend: onFile ? SecretBackendKind.File : service.Backend,
            Detail: onFile ? file.Detail : service.Detail,
            Locked: service.Locked,
            Unencrypted: onFile,
            Writable: _serviceAbsent ? file.Writable : service is { Writable: true, Locked: false },
            OverriddenKeys: service.OverriddenKeys);
    }

    private async ValueTask MoveFileCopyIntoServiceAsync(ISecretStore service, SecretKey key, string value, CancellationToken cancellationToken)
    {
        var written = await service.SetAsync(key, value, cancellationToken);
        if (written is not SecretWriteResult.Written)
        {
            // The file copy is the only one that is current; keep it until the keyring takes it.
            _logger.LogWarning("Could not move {Key} from the secrets file into the keyring: {Result}", key, written);
            return;
        }

        await RemoveFileCopyAsync(key, cancellationToken);
        _logger.LogInformation("Moved {Key} from the secrets file into the keyring", key);
    }

    private async ValueTask RemoveFileCopyAsync(SecretKey key, CancellationToken cancellationToken)
    {
        var removed = await _file.DeleteAsync(key, cancellationToken);
        if (removed is SecretWriteResult.Unavailable unavailable)
            _logger.LogWarning("Could not remove {Key} from the secrets file: {Reason}", key, unavailable.Reason);
    }

    private async ValueTask<SecretWriteResult> WriteToFileAsync(SecretKey key, string value, CancellationToken cancellationToken)
    {
        var result = await _file.SetAsync(key, value, cancellationToken);
        if (result is not SecretWriteResult.Written) return result;

        if (Interlocked.Exchange(ref _warnedAboutFile, 1) == 0)
        {
            var file = await _file.DescribeAsync(cancellationToken);
            _logger.LogWarning("No keyring answered, so secrets are stored unencrypted in {Path}", file.Detail);
        }
        return result;
    }

    private void NoteServiceAnswer(SecretUnavailableKind? unavailable)
        => _serviceAbsent = unavailable is SecretUnavailableKind.NoBus or SecretUnavailableKind.ServiceAbsent;
}

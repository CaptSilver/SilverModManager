using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using NexusMods.Paths;
using SilverModManager.Sdk;
using R3;

namespace SilverModManager.Secrets.File;

/// <summary>
/// Keeps secrets in <c>secrets.json</c> next to the app's other data: a flat JSON object of key to value.
/// The file is created owner-only from the start and is never chmod'ed afterwards; a copy that has
/// group or other access, is a symlink or belongs to another user is refused rather than repaired,
/// because the secrets in it may already have been read by someone else.
/// </summary>
public sealed class FileSecretStore : ISecretStore, IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode GroupAndOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The file next to <c>secrets.json</c> that processes lock while they read, change and replace it. It holds
    /// nothing and is never deleted: removing a lock file lets two processes lock two different files.
    /// </summary>
    internal const string LockFileName = "secrets.json.lock";

    private readonly ILogger<FileSecretStore> _logger;
    private readonly TimeSpan _lockTimeout;
    private readonly uint _effectiveUid;
    private readonly string _directory;
    private readonly string _path;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Subject<SecretKey> _changed = new();

    public FileSecretStore(IFileSystem fileSystem, ILogger<FileSecretStore> logger) : this(fileSystem, logger, UnixFileOwner.EffectiveUid()) { }

    // The uid is a parameter so tests can stand in for a file owned by someone else, which needs root to set up.
    internal FileSecretStore(IFileSystem fileSystem, ILogger<FileSecretStore> logger, uint effectiveUid, TimeSpan? lockTimeout = null)
    {
        _logger = logger;
        _effectiveUid = effectiveUid;
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
        var directory = fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine(ApplicationConstants.DataDirectoryName);
        _directory = directory.ToString();
        _path = directory.Combine("secrets.json").ToString();
        _lockPath = directory.Combine(LockFileName).ToString();
    }

    public Observable<SecretKey> Changed => _changed;

    public Observable<SecretStoreStatus> StatusChanged => Observable.Never<SecretStoreStatus>();

    public async ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var (entries, unavailable) = Read();
            if (unavailable is not null) return unavailable;
            return entries.TryGetValue(key.Name, out var value) ? new SecretResult.Found(value) : new SecretResult.NotFound();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var (processLock, lockFailure) = await AcquireProcessLockAsync(cancellationToken);
            if (lockFailure is not null) return lockFailure;
            using (processLock)
            {
                var (entries, unavailable) = Read();
                if (unavailable is not null) return new SecretWriteResult.Unavailable(unavailable.Kind, unavailable.Reason);

                entries[key.Name] = value;
                if (Write(entries) is { } failure) return failure;
            }
        }
        finally
        {
            _lock.Release();
        }

        _changed.OnNext(key);
        return new SecretWriteResult.Written();
    }

    public async ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Nothing to delete from, and nothing worth creating the directory and lock file for.
            if (!Directory.Exists(_directory)) return new SecretWriteResult.Written();

            var (processLock, lockFailure) = await AcquireProcessLockAsync(cancellationToken);
            if (lockFailure is not null) return lockFailure;
            using (processLock)
            {
                var (entries, unavailable) = Read();
                if (unavailable is not null) return new SecretWriteResult.Unavailable(unavailable.Kind, unavailable.Reason);
                if (!entries.Remove(key.Name)) return new SecretWriteResult.Written();

                if (Write(entries) is { } failure) return failure;
            }
        }
        finally
        {
            _lock.Release();
        }

        _changed.OnNext(key);
        return new SecretWriteResult.Written();
    }

    public async ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var (entries, unavailable) = Read();
            if (unavailable is not null)
            {
                // The contract has no way to say "unavailable" from a listing; Get and Set still report the reason.
                _logger.LogWarning("Listing no secrets because the store is unavailable: {Reason}", unavailable.Reason);
                return [];
            }

            var keys = new List<SecretKey>(entries.Count);
            foreach (var name in entries.Keys)
            {
                try
                {
                    keys.Add(new SecretKey(name));
                }
                catch (ArgumentException)
                {
                    _logger.LogWarning("Ignoring entry '{Name}' in {Path}: not a valid secret key", name, _path);
                }
            }
            return keys;
        }
        finally
        {
            _lock.Release();
        }
    }

    public ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken cancellationToken) => new(Describe());

    // The file has nothing to unlock.
    public ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken cancellationToken) => new(Describe());

    private SecretStoreStatus Describe() => new(SecretBackendKind.File, _path, Locked: false, Unencrypted: true, Writable: CanWrite(), []);

    /// <summary>
    /// Whether a Set would get as far as writing: the file passes the checks a read makes, and the nearest
    /// directory that exists lets this user create files in it, and the lock file, when there is one, can be
    /// opened for reading and writing.
    /// </summary>
    private bool CanWrite()
    {
        if (Read().Unavailable is not null) return false;
        if (System.IO.File.Exists(_lockPath) && !UnixFileAccess.CanReadWrite(_lockPath)) return false;

        var directory = _directory;
        while (!Directory.Exists(directory))
        {
            if (Path.GetDirectoryName(directory) is not { Length: > 0 } parent) return false;
            directory = parent;
        }

        return UnixFileAccess.CanCreateIn(directory);
    }

    /// <summary>
    /// Takes the cross-process lock, creating the app directory and the lock file when they are missing. The
    /// in-process semaphore is not enough: the app and the CLI each run their own store over the same file, and
    /// without this their read-modify-write cycles interleave and the later rename drops the earlier write.
    /// </summary>
    /// <remarks>
    /// .NET maps <see cref="FileShare.None"/> on Unix to an advisory flock, so the open fails while any other
    /// open file description holds it, in this process or another, and the kernel drops it if the holder dies.
    /// </remarks>
    private async ValueTask<(FileStream? Lock, SecretWriteResult.Unavailable? Failure)> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(_directory);
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, UnixCreateMode = OwnerOnly };
            while (true)
            {
                try
                {
                    return (new FileStream(_lockPath, options), null);
                }
                catch (IOException) when (deadline.Elapsed < _lockTimeout && System.IO.File.Exists(_lockPath))
                {
                    // Held by someone else (an unrelated IO failure would not leave the file in place and also time out below).
                    await Task.Delay(LockPollInterval, cancellationToken);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Could not lock {Path}", _lockPath);
            return (null, new SecretWriteResult.Unavailable(SecretUnavailableKind.Faulted,
                $"Could not write {_path} because another Silver Mod Manager process is using it or {_lockPath} cannot be locked: {e.Message}"));
        }
    }

    public void Dispose()
    {
        _changed.Dispose();
        _lock.Dispose();
    }

    /// <summary>
    /// Reads the whole file after checking it is safe to trust. A missing file is an empty store.
    /// </summary>
    private (Dictionary<string, string> Entries, SecretResult.Unavailable? Unavailable) Read()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var info = new FileInfo(_path);
            // LinkTarget first: a dangling link has Exists == false and would otherwise look like an empty store.
            if (info.LinkTarget is not null)
                return (entries, Faulted($"{_path} is a symbolic link; secrets are only read from a regular file owned by you. Replace it with a regular file, then: chmod 600 {_path}"));
            if (!info.Exists) return (entries, null);

            var mode = info.UnixFileMode;
            if ((mode & GroupAndOther) != 0)
                return (entries, Faulted($"{_path} has mode {FormatMode(mode)} and could be read by other users. Restrict it: chmod 600 {_path}"));

            var owner = UnixFileOwner.GetOwner(_path);
            if (owner != _effectiveUid)
                return (entries, Faulted($"{_path} is owned by uid {owner}, not by you (uid {_effectiveUid}). Take it over or remove it, then: chmod 600 {_path}"));

            var json = System.IO.File.ReadAllText(_path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null) return (entries, Malformed($"{_path} does not contain a JSON object of secrets."));
            foreach (var (name, value) in parsed) entries[name] = value;
            return (entries, null);
        }
        catch (JsonException e)
        {
            return (entries, Malformed($"{_path} is not a JSON object of strings: {e.Message}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Could not read {Path}", _path);
            return (entries, Faulted($"Could not read {_path}: {e.Message}"));
        }
    }

    /// <summary>
    /// Writes <paramref name="entries"/> to a sibling temp file created owner-only and moves it over the
    /// real one, so the file is never visible torn or with a looser mode. No entries removes the file.
    /// </summary>
    private SecretWriteResult.Unavailable? Write(Dictionary<string, string> entries)
    {
        string? temp = null;
        try
        {
            if (entries.Count == 0)
            {
                System.IO.File.Delete(_path);
                return null;
            }

            // An existing app data directory keeps whatever mode it has; the file mode is what protects the secrets.
            Directory.CreateDirectory(_directory);
            temp = $"{_path}.tmp-{Path.GetRandomFileName()}";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = OwnerOnly };
            using (var stream = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(stream, entries, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }

            System.IO.File.Move(temp, _path, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Could not write {Path}", _path);
            if (temp is not null) TryDelete(temp);
            return new SecretWriteResult.Unavailable(SecretUnavailableKind.Faulted, $"Could not write {_path}: {e.Message}");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The write failure is what gets reported; a leftover temp file is the lesser problem.
            _logger.LogWarning(e, "Could not remove the temporary file {Path}", path);
        }
    }

    private static string FormatMode(UnixFileMode mode) => Convert.ToString((int)mode & 0xFFF, 8).PadLeft(3, '0');

    private static SecretResult.Unavailable Faulted(string reason) => new(SecretUnavailableKind.Faulted, reason);

    private static SecretResult.Unavailable Malformed(string reason) => new(SecretUnavailableKind.Malformed, reason);
}

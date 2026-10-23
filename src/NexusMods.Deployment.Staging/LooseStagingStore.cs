using System.Globalization;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Native;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;
using NexusMods.Sdk.Threading;

namespace NexusMods.Deployment.Staging;

/// <summary>
/// Stages files from <see cref="IFileStore"/> as plain files under the configured location, and under a folder
/// on each other filesystem a game folder is on (see <see cref="ForRoot"/>).
/// </summary>
public sealed class LooseStagingStore : ILooseStagingStore
{
    /// <summary>
    /// Where a store sits on a filesystem other than the default one, relative to that filesystem's root. The
    /// folder is per user: the root of a second drive is often shared, and another account must not be able to
    /// plant the folder (or a link in its place) that this one stages files into.
    /// </summary>
    internal static string FilesystemStoreFolder => $".silvermodmanager-{LibcUser.EffectiveUserId}/staging";

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // Lists the per-filesystem stores this one has created, one path per line, so a sweep after a restart
    // still reaches them. It lives in the store root, where nothing parses it as an installation.
    private const string KnownStoresFile = ".filesystem-stores";

    private readonly IFileStore _fileStore;
    private readonly IFileSystem _fileSystem;
    private readonly IFilesystemLocator _locator;
    private readonly bool _perFilesystem;
    private readonly ILogger<LooseStagingStore> _logger;

    // Holds are the read side and Sweep the write side, so any number of applies can stage at once
    // while a sweep sees the store quiet. Per-filesystem stores share the lock: an apply holds the default
    // store and may stage in any of them.
    private readonly AsyncFriendlyReaderWriterLock _sweepLock;

    // Set on a per-filesystem store, which hands ForRoot to the default store that created it.
    private readonly LooseStagingStore? _parent;

    // Default store only. Guarded by itself.
    private readonly Dictionary<ulong, LooseStagingStore> _byDevice = new();
    private readonly Dictionary<AbsolutePath, LooseStagingStore> _known = new();
    private readonly HashSet<ulong> _refusedDevices = [];

    // One gate per destination: concurrent Ensure calls for the same file share a temp name,
    // so they must not extract at the same time. An entry goes when its last user leaves; dropping
    // it earlier would let a newcomer extract beside a caller still queued on the old gate.
    private readonly Dictionary<AbsolutePath, Gate> _gates = new();

    private sealed class Gate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    public LooseStagingStore(ISettingsManager settingsManager, IFileStore fileStore, IFileSystem fileSystem, IFilesystemLocator locator, ILogger<LooseStagingStore> logger)
    {
        var settings = settingsManager.Get<LooseStagingStoreSettings>();
        _fileStore = fileStore;
        _fileSystem = fileSystem;
        _locator = locator;
        _logger = logger;
        _perFilesystem = settings.PerFilesystemStores;
        _sweepLock = new AsyncFriendlyReaderWriterLock();
        Root = settings.Location.ToPath(fileSystem);
    }

    private LooseStagingStore(LooseStagingStore parent, AbsolutePath root)
    {
        _fileStore = parent._fileStore;
        _fileSystem = parent._fileSystem;
        _locator = parent._locator;
        _logger = parent._logger;
        _perFilesystem = parent._perFilesystem;
        _sweepLock = parent._sweepLock;
        _parent = parent;
        Root = root;
    }

    /// <inheritdoc/>
    public AbsolutePath Root { get; }

    /// <inheritdoc/>
    public ILooseStagingStore ForRoot(AbsolutePath root)
    {
        if (_parent is not null) return _parent.ForRoot(root);
        if (!_perFilesystem) return this;

        if (_locator.DeviceOf(Root) is not { } home || _locator.DeviceOf(root) is not { } device || device == home) return this;

        lock (_byDevice)
        {
            if (_byDevice.TryGetValue(device, out var existing)) return existing;
            if (_refusedDevices.Contains(device)) return this;

            var created = TryCreateFilesystemStore(root);
            if (created is null)
            {
                _refusedDevices.Add(device);
                return this;
            }

            _byDevice[device] = created;
            return created;
        }
    }

    private LooseStagingStore? TryCreateFilesystemStore(AbsolutePath root)
    {
        if (_locator.RootOf(root) is not { } filesystemRoot) return null;

        var storeRoot = filesystemRoot.Combine(FilesystemStoreFolder);
        try
        {
            // A folder from an earlier run is reused, but only after it passes the same check as a new one. Each level
            // is checked before anything is created inside it, so nothing lands behind a planted link.
            CreateOwnerOnly(storeRoot.Parent);
            var reason = UntrustedFolderReason(storeRoot.Parent);
            if (reason is null)
            {
                CreateOwnerOnly(storeRoot);
                reason = UntrustedFolderReason(storeRoot);
            }
            if (reason is not null)
            {
                _logger.LogWarning("Not using the staging store at {Path}: {Reason}; files for {Root} are staged in {Default}, so linking there may fall back to symlinks",
                    storeRoot, reason, root, Root);
                return null;
            }

            var probe = storeRoot.Combine($".{ProtonMessages.ProbeFileName}-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe.ToString(), []);
            File.Delete(probe.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Cannot keep a staging store at {Path} ({Reason}); files for {Root} are staged in {Default}, so linking there may fall back to symlinks",
                storeRoot, e.Message, root, Root);
            return null;
        }

        var store = new LooseStagingStore(this, storeRoot);
        lock (_known)
        {
            _known[storeRoot] = store;
            RememberStore(storeRoot);
        }
        return store;
    }

    // Never changes the mode of a folder that is already there: one this user didn't make stays as it is and fails
    // the check instead.
    private static void CreateOwnerOnly(AbsolutePath folder)
    {
        if (LibcStat.TryLStat(folder.ToString(), out _) == 0) return;
        Directory.CreateDirectory(folder.ToString(), OwnerOnly);
    }

    /// <summary>
    /// Why a per-filesystem store folder can't be trusted, or null when it can: the store folder and its per-user
    /// parent must both be real folders (not links), owned by this user, that nobody else can write to.
    /// </summary>
    internal static string? UntrustedReason(AbsolutePath storeRoot)
        => UntrustedFolderReason(storeRoot.Parent) ?? UntrustedFolderReason(storeRoot);

    private static string? UntrustedFolderReason(AbsolutePath folder)
    {
        var errno = LibcStat.TryLStat(folder.ToString(), out var stat);
        if (errno != 0) return $"{folder} can't be read (errno {errno})";
        if (!stat.IsDirectory) return $"{folder} is not a plain folder";
        var uid = LibcUser.EffectiveUserId;
        if (stat.Uid != uid) return $"{folder} belongs to user {stat.Uid}, not {uid}";
        // Group or other write bit.
        if ((stat.Mode & 0b000_010_010) != 0) return $"{folder} can be written by other users";
        return null;
    }

    private AbsolutePath KnownStoresPath => Root.Combine(KnownStoresFile);

    private void RememberStore(AbsolutePath storeRoot)
    {
        var path = KnownStoresPath;
        var lines = path.FileExists ? File.ReadAllLines(path.ToString()) : [];
        if (lines.Contains(storeRoot.ToString(), StringComparer.Ordinal)) return;

        Root.CreateDirectory();
        File.AppendAllLines(path.ToString(), [storeRoot.ToString()]);
    }

    // Stores made by this run plus any recorded by an earlier one.
    private List<LooseStagingStore> PerFilesystemStores()
    {
        lock (_known)
        {
            if (KnownStoresPath.FileExists)
            {
                foreach (var line in File.ReadAllLines(KnownStoresPath.ToString()))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var storeRoot = _fileSystem.FromUnsanitizedFullPath(line.Trim());
                    if (_known.ContainsKey(storeRoot)) continue;

                    // A store that is gone is an unmounted filesystem, which the sweep skips on its own. One that is
                    // there but no longer passes the check (swapped for a link, opened to others) is never swept:
                    // deleting through it could reach files that aren't the store's.
                    if (LibcStat.TryLStat(storeRoot.ToString(), out _) == 0 && UntrustedReason(storeRoot) is { } reason)
                    {
                        _logger.LogWarning("Not sweeping the staging store at {Path}: {Reason}", storeRoot, reason);
                        continue;
                    }
                    _known[storeRoot] = new LooseStagingStore(this, storeRoot);
                }
            }
            return _known.Values.ToList();
        }
    }

    internal int GateCount { get { lock (_gates) return _gates.Count; } }

    /// <inheritdoc/>
    public AbsolutePath PathFor(GameInstallMetadataId installation, Hash hash)
    {
        var name = HashToName(hash);
        return InstallationDirectory(installation).Combine(name[..2]).Combine(name);
    }

    /// <inheritdoc/>
    public async ValueTask<AbsolutePath> Ensure(GameInstallMetadataId installation, Hash hash, CancellationToken token = default)
    {
        var path = PathFor(installation, hash);
        if (path.FileExists) return path;

        var gate = EnterGate(path);
        try
        {
            await gate.Semaphore.WaitAsync(token);
        }
        catch
        {
            LeaveGate(path, gate);
            throw;
        }

        try
        {
            // Another caller may have finished while this one waited for the gate.
            if (path.FileExists) return path;

            path.Parent.CreateDirectory();

            // Extract beside the final name and rename, so a crash never leaves a partial file under it.
            var temp = path.Parent.Combine($".{path.FileName}.tmp");
            if (temp.FileExists) temp.Delete();

            _logger.LogDebug("Staging {Hash} for {Installation} at {Path}", hash, installation, path);
            await _fileStore.ExtractFiles([(hash, temp)], token);
            await temp.MoveToAsync(path, overwrite: true, token);
            return path;
        }
        finally
        {
            gate.Semaphore.Release();
            LeaveGate(path, gate);
        }
    }

    private Gate EnterGate(AbsolutePath path)
    {
        lock (_gates)
        {
            if (!_gates.TryGetValue(path, out var gate)) _gates[path] = gate = new Gate();
            gate.Users++;
            return gate;
        }
    }

    private void LeaveGate(AbsolutePath path, Gate gate)
    {
        lock (_gates)
        {
            if (--gate.Users == 0) _gates.Remove(path);
        }
    }

    /// <inheritdoc/>
    public bool Contains(GameInstallMetadataId installation, Hash hash) => PathFor(installation, hash).FileExists;

    /// <inheritdoc/>
    public IEnumerable<(Hash Hash, AbsolutePath Path)> Enumerate(GameInstallMetadataId installation)
    {
        var directory = InstallationDirectory(installation);
        if (!directory.DirectoryExists()) yield break;

        foreach (var file in directory.EnumerateFiles(recursive: true))
        {
            if (TryParseHash(file.FileName, out var hash)) yield return (hash, file);
        }
    }

    /// <inheritdoc/>
    public IEnumerable<GameInstallMetadataId> Installations()
    {
        if (!Root.DirectoryExists()) yield break;

        foreach (var directory in Root.EnumerateDirectories(recursive: false))
        {
            if (TryParseInstallation(directory.FileName, out var installation)) yield return installation;
        }
    }

    /// <inheritdoc/>
    public AbsolutePath DirectoryFor(GameInstallMetadataId installation)
    {
        var directory = InstallationDirectory(installation);
        directory.CreateDirectory();
        return directory;
    }

    /// <inheritdoc/>
    public bool Release(GameInstallMetadataId installation, Hash hash)
    {
        var path = PathFor(installation, hash);
        if (!path.FileExists) return false;
        path.Delete();
        return true;
    }

    /// <inheritdoc/>
    public IDisposable Hold() => new SweepHold(_sweepLock.ReadLock());

    // The lock's read handle decrements on every Dispose, so a hold disposed twice would release
    // someone else's read and let a sweep run under it.
    private sealed class SweepHold(AsyncFriendlyReaderWriterLock.ReadLockDisposable readLock) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) readLock.Dispose();
        }
    }

    /// <inheritdoc/>
    public int Sweep(Func<IReadOnlyDictionary<GameInstallMetadataId, IReadOnlySet<Hash>>> liveFactory)
    {
        if (_parent is not null) return _parent.Sweep(liveFactory);

        using var exclusive = _sweepLock.WriteLock();

        IReadOnlyDictionary<GameInstallMetadataId, IReadOnlySet<Hash>>? live = null;
        var sweepStart = default(DateTime);
        var deleted = 0;
        foreach (var store in new[] { this }.Concat(PerFilesystemStores()))
        {
            if (!store.Root.DirectoryExists())
            {
                // A filesystem that is not mounted looks like an empty mount point. Its files are not ours to judge.
                if (store != this) _logger.LogInformation("Skipping the staging store at {Path}: its filesystem is not mounted", store.Root);
                continue;
            }

            // Read after the lock is ours: a holder that just finished has committed what it staged by now.
            if (live is null)
            {
                live = liveFactory();
                sweepStart = DateTime.UtcNow;
            }
            deleted += store.SweepOne(live, sweepStart);
        }

        if (deleted > 0) _logger.LogInformation("Removed {Count} unreferenced staged files", deleted);
        return deleted;
    }

    private int SweepOne(IReadOnlyDictionary<GameInstallMetadataId, IReadOnlySet<Hash>> live, DateTime sweepStart)
    {
        var deleted = 0;
        foreach (var installation in Installations().ToArray())
        {
            var directory = InstallationDirectory(installation);

            if (!live.TryGetValue(installation, out var liveHashes))
            {
                deleted += Enumerate(installation).Count();
                directory.DeleteDirectory(recursive: true);
                continue;
            }

            foreach (var (hash, path) in Enumerate(installation).ToArray())
            {
                if (liveHashes.Contains(hash)) continue;
                path.Delete();
                deleted++;
            }

            // A crashed extraction or a probe that never got cleaned up is never parsed as a staged
            // file, so nothing else would remove it, and it would keep its directories alive.
            foreach (var file in directory.EnumerateFiles(recursive: true).ToArray())
            {
                if (IsLeftover(file.FileName) && file.FileInfo.LastWriteTimeUtc < sweepStart) file.Delete();
            }

            foreach (var prefix in directory.EnumerateDirectories(recursive: false).ToArray())
            {
                if (!prefix.EnumerateFiles(recursive: false).Any()) prefix.DeleteDirectory(recursive: false);
            }

            if (!directory.EnumerateFiles(recursive: true).Any() && !directory.EnumerateDirectories(recursive: false).Any())
                directory.DeleteDirectory(recursive: false);
        }

        return deleted;
    }

    private AbsolutePath InstallationDirectory(GameInstallMetadataId installation)
        => Root.Combine(installation.Value.Value.ToString("x", CultureInfo.InvariantCulture));

    private static bool IsLeftover(string name)
        => name.StartsWith($"{ProtonMessages.ProbeFileName}-", StringComparison.Ordinal)
           || (name.Length == 16 + 5 && name[0] == '.' && name.EndsWith(".tmp", StringComparison.Ordinal) && TryParseHash(name[1..^4], out _));

    private static string HashToName(Hash hash) => hash.Value.ToString("x16", CultureInfo.InvariantCulture);

    private static bool TryParseHash(string name, out Hash hash)
    {
        // Temp files start with a dot and never parse.
        if (name.Length == 16 && ulong.TryParse(name, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            hash = Hash.From(value);
            return true;
        }
        hash = default;
        return false;
    }

    private static bool TryParseInstallation(string name, out GameInstallMetadataId installation)
    {
        if (ulong.TryParse(name, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            installation = GameInstallMetadataId.From(EntityId.From(value));
            return true;
        }
        installation = default;
        return false;
    }
}

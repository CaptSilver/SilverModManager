using System.Globalization;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;
using NexusMods.Sdk.Threading;

namespace NexusMods.Deployment.Staging;

/// <summary>
/// Stages files from <see cref="IFileStore"/> as plain files under the configured location.
/// </summary>
public sealed class LooseStagingStore : ILooseStagingStore
{
    private readonly IFileStore _fileStore;
    private readonly ILogger<LooseStagingStore> _logger;

    // Holds are the read side and Sweep the write side, so any number of applies can stage at once
    // while a sweep sees the store quiet.
    private readonly AsyncFriendlyReaderWriterLock _sweepLock = new();

    // One gate per destination: concurrent Ensure calls for the same file share a temp name,
    // so they must not extract at the same time. An entry goes when its last user leaves; dropping
    // it earlier would let a newcomer extract beside a caller still queued on the old gate.
    private readonly Dictionary<AbsolutePath, Gate> _gates = new();

    private sealed class Gate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    public LooseStagingStore(ISettingsManager settingsManager, IFileStore fileStore, IFileSystem fileSystem, ILogger<LooseStagingStore> logger)
    {
        _fileStore = fileStore;
        _logger = logger;
        Root = settingsManager.Get<LooseStagingStoreSettings>().Location.ToPath(fileSystem);
    }

    /// <inheritdoc/>
    public AbsolutePath Root { get; }

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
        using var exclusive = _sweepLock.WriteLock();
        if (!Root.DirectoryExists()) return 0;

        // Read after the lock is ours: a holder that just finished has committed what it staged by now.
        var live = liveFactory();
        var sweepStart = DateTime.UtcNow;

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

        if (deleted > 0) _logger.LogInformation("Removed {Count} unreferenced staged files", deleted);
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

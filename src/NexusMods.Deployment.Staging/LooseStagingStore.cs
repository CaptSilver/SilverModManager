using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;

namespace NexusMods.Deployment.Staging;

/// <summary>
/// Stages files from <see cref="IFileStore"/> as plain files under the configured location.
/// </summary>
public sealed class LooseStagingStore : ILooseStagingStore
{
    private readonly IFileStore _fileStore;
    private readonly ILogger<LooseStagingStore> _logger;

    // One gate per destination: concurrent Ensure calls for the same file share a temp name,
    // so they must not extract at the same time. Entries stay for the life of the process.
    private readonly ConcurrentDictionary<AbsolutePath, SemaphoreSlim> _gates = new();

    public LooseStagingStore(ISettingsManager settingsManager, IFileStore fileStore, IFileSystem fileSystem, ILogger<LooseStagingStore> logger)
    {
        _fileStore = fileStore;
        _logger = logger;
        Root = settingsManager.Get<LooseStagingStoreSettings>().Location.ToPath(fileSystem);
    }

    /// <inheritdoc/>
    public AbsolutePath Root { get; }

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

        var gate = _gates.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
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
            gate.Release();
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
    public int Sweep(IReadOnlyDictionary<GameInstallMetadataId, IReadOnlySet<Hash>> live)
    {
        if (!Root.DirectoryExists()) return 0;

        var deleted = 0;
        foreach (var installation in Installations().ToArray())
        {
            var directory = InstallationDirectory(installation);

            if (!live.TryGetValue(installation, out var liveHashes))
            {
                deleted += directory.EnumerateFiles(recursive: true).Count();
                directory.DeleteDirectory(recursive: true);
                continue;
            }

            foreach (var (hash, path) in Enumerate(installation).ToArray())
            {
                if (liveHashes.Contains(hash)) continue;
                path.Delete();
                deleted++;
            }

            foreach (var prefix in directory.EnumerateDirectories(recursive: false).ToArray())
            {
                if (!prefix.EnumerateFiles(recursive: false).Any()) prefix.DeleteDirectory(recursive: false);
            }
        }

        if (deleted > 0) _logger.LogInformation("Removed {Count} unreferenced staged files", deleted);
        return deleted;
    }

    private AbsolutePath InstallationDirectory(GameInstallMetadataId installation)
        => Root.Combine(installation.Value.Value.ToString("x", CultureInfo.InvariantCulture));

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

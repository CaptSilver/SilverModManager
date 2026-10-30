using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;

namespace SilverModManager.DataModel;

/// <summary>
/// A readonly stream source that reads files from game files on-disk. This is useful for when
/// you need to read a game file during a diagnostic, but don't care if the file is backed up or
/// not.
/// </summary>
public class GameFileStreamSource : IIdentityStreamSource
{
    private readonly IConnection _conn;
    private readonly IGameRegistry _gameRegistry;

    public GameFileStreamSource(IConnection conn, IServiceProvider provider)
    {
        _conn = conn;
        _gameRegistry = provider.GetRequiredService<IGameRegistry>();
    }

    /// <summary>
    /// Opens the game file whose disk state row carries this identity, of whatever kind.
    /// </summary>
    public ValueTask<Stream?> OpenAsync(FileIdentity identity, CancellationToken cancellationToken = default)
    {
        if (Resolve(identity) is not { } path)
            return ValueTask.FromResult<Stream?>(null);
        return ValueTask.FromResult<Stream?>(path.Open(mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read));
    }

    public bool Exists(FileIdentity identity)
    {
        return Resolve(identity) != null;
    }

    /// <summary>
    /// The file on disk whose disk state row carries this identity and still has the size and modified time the row recorded.
    /// </summary>
    public AbsolutePath? Resolve(FileIdentity identity)
    {
        // An xxHash3 row keeps its digest in the Hash column, which is also all a row written before identities
        // carried their kind has. Any other kind is found by its stored identity, compared as hex because the
        // query engine has no blob comparison.
        var options = identity.TryXxHash3(out var hash)
            ? _conn.Query<(EntityId Game, LocationId Location, RelativePath Path, DateTimeOffset LastModified, Size Size)>(
                // Path is (owning game, location, relative path); the owning game is already the Game column.
                $"select Game, Path.Item2, Path.Item3, LastModified, Size from mdb_DiskStateEntry(Db=>{_conn}) WHERE Hash = {hash} ORDER BY LastModified DESC").ToList()
            : _conn.Query<(EntityId Game, LocationId Location, RelativePath Path, DateTimeOffset LastModified, Size Size)>(
                $"select Game, Path.Item2, Path.Item3, LastModified, Size from mdb_DiskStateEntry(Db=>{_conn}) WHERE hex(Identity) = {identity.ToHex()} ORDER BY LastModified DESC").ToList();

        foreach (var option in options)
        {
            var metadata = GameInstallMetadata.Load(_conn.Db, option.Game);
            if (!metadata.IsValid()) continue;
            if (!TryGetInstallation(metadata, out var installation)) continue;

            var resolvedPath = installation.Locations.ToAbsolutePath(new GamePath(option.Location, option.Path));
            if (!resolvedPath.FileExists) continue;
            try
            {
                var info = FileFingerprint.Of(resolvedPath);
                if (info.LastModified != option.LastModified || info.Size != option.Size)
                    continue;

                return resolvedPath;
            }
            catch (Exception)
            {
                continue;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the installation by its metadata row. Going through the last synced loadout would fail for a
    /// game that has not been synchronized yet, and the first synchronize is when plugins are read.
    /// </summary>
    private bool TryGetInstallation(GameInstallMetadata.ReadOnly metadata, [NotNullWhen(true)] out GameInstallation? installation)
    {
        foreach (var candidate in _gameRegistry.LocateGameInstallations())
        {
            if (!_gameRegistry.TryGetMetadata(candidate, out var candidateMetadata) || candidateMetadata.Id != metadata.Id) continue;
            installation = candidate;
            return true;
        }

        installation = null;
        return false;
    }

    public SourcePriority Priority => SourcePriority.Local;
}

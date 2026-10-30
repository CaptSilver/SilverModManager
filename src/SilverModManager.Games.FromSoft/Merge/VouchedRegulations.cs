using SilverModManager.Sdk.Hashes;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Games;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>The content identities of regulations the app can vouch for as the game's own, as the library knows files.</summary>
public interface IVouchedRegulations
{
    /// <summary>Every xxHash3 identity the app would accept as a vanilla regulation of <paramref name="installation"/>'s game.</summary>
    IReadOnlySet<FileIdentity> Hashes(GameInstallation installation);
}

/// <summary>
/// Three sources of vanilla regulation hashes: the baselines the app kept, the regulation of the installed build, and
/// the game file hash database. A library archive's file is only ever a vanilla copy if its hash is in here, so a
/// mod's own regulation never qualifies.
/// <para>
/// The database part walks every build it knows, so it is computed once and recomputed when the set of databases
/// changes. The live part is remembered by the regulation file's size and write time, because identifying the build
/// decrypts the whole file. A game the database does not cover contributes nothing.
/// </para>
/// </summary>
public sealed class VouchedRegulations(IFileHashesService fileHashes, RegulationBaselines baselines, InstalledBuilds builds, IFileIdentityResolver resolver, ILogger<VouchedRegulations> logger) : IVouchedRegulations
{
    private readonly object _gate = new();
    private (string Key, IDb[] Databases, IReadOnlySet<FileIdentity> Hashes)? _fromDatabase;
    private (string Key, FileIdentity? Identity)? _live;

    /// <inheritdoc />
    public IReadOnlySet<FileIdentity> Hashes(GameInstallation installation)
    {
        var all = new HashSet<FileIdentity>(baselines.VouchedHashes());
        all.UnionWith(FromDatabase(installation));
        if (Live(installation) is { } live) all.Add(live);
        return all;
    }

    private IReadOnlySet<FileIdentity> FromDatabase(GameInstallation installation)
    {
        if (installation.Game is not IFromSoftGame game || game.Formats.RegulationFileName is not { } fileName) return new HashSet<FileIdentity>();
        if (!installation.Game.NexusModsGameId.HasValue) return new HashSet<FileIdentity>();

        var gameId = installation.Game.NexusModsGameId.Value;
        var store = installation.LocatorResult.Store;
        var databases = fileHashes.Databases.ToArray();
        var key = $"{gameId}/{store}/" + string.Join(',', databases.Select(db => db.BasisTxId));

        lock (_gate)
        {
            if (_fromDatabase is { } cached && cached.Key == key && SameInstances(cached.Databases, databases)) return cached.Hashes;

            var regulationPath = game.Layout.GameSubfolder + "/" + fileName;
            var hashes = new HashSet<FileIdentity>();
            foreach (var version in fileHashes.GetKnownVanityVersions(gameId))
            {
                if (!fileHashes.TryGetLocatorIdsForVanityVersion(store, version, out var ids)) continue;
                foreach (var record in fileHashes.GetGameFiles((store, ids)))
                {
                    if (record.Path.LocationId == LocationId.Game && record.Path.Path.Path.Equals(regulationPath, StringComparison.OrdinalIgnoreCase))
                        // The set is compared against the xxHash3 identities of files in the library; a record named
                        // by another digest vouches through the store key a recorded relation joins it to.
                        foreach (var candidate in resolver.StoreKeyCandidates(record.Identity, record.Size)) hashes.Add(candidate);
                }
            }

            _fromDatabase = (key, databases, hashes);
            return hashes;
        }
    }

    // A hash code can collide between two databases; the instances cannot.
    private static bool SameInstances(IDb[] a, IDb[] b)
        => a.Length == b.Length && a.Zip(b).All(pair => ReferenceEquals(pair.First, pair.Second));

    private FileIdentity? Live(GameInstallation installation)
    {
        if (installation.Game is not IFromSoftGame game || game.Formats.RegulationFileName is not { } fileName) return null;

        var file = FromSoftPaths.GameFolder(game.Layout, installation) / fileName;
        if (!file.FileExists) return null;

        var info = new FileInfo(file.GetFullPath());
        var key = $"{file.GetFullPath()}/{info.Length}/{info.LastWriteTimeUtc.Ticks}";
        lock (_gate)
        {
            if (_live is { } cached && cached.Key == key) return cached.Identity;

            FileIdentity? live = null;
            try
            {
                var identity = builds.Identify(installation);
                var bytes = File.ReadAllBytes(file.GetFullPath());
                // The identity may be a cached reading; a file that changed since must not be vouched for under it.
                if (identity.RegulationHash is { } identified && Regulation.Sha256Hex(bytes).Equals(identified, StringComparison.OrdinalIgnoreCase))
                    live = FileIdentity.XxHash3(bytes.xxHash3());
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(exception, "The installed regulation could not be identified, so it vouches for nothing");
            }

            _live = (key, live);
            return live;
        }
    }
}

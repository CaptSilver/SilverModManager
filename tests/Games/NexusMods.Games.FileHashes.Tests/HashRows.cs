using OperatingSystem = NexusMods.Abstractions.Games.FileHashes.Values.OperatingSystem;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.GOG.Values;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Games.FileHashes.Tests;

/// <summary>
/// Writes the rows the file_hashes queries and the service lookups read. Every method commits one
/// transaction to the connection it is given, so a row only ever refers to rows of its own store.
/// </summary>
internal static class HashRows
{
    public static async Task<EntityId> AddSteamManifest(IConnection store, ulong manifestId, params (string Path, string Content)[] files)
    {
        var hashes = await HashAll(files);
        using var tx = store.BeginTransaction();
        var pathIds = AddPaths(tx, hashes);
        var manifest = new SteamManifest.New(tx)
        {
            AppId = AppId.From(1u),
            DepotId = DepotId.From(2u),
            ManifestId = ManifestId.From(manifestId),
            Name = "public",
            FilesIds = pathIds,
        };
        var result = await tx.Commit();
        return result[manifest.Id];
    }

    /// <summary>One build with one depot in en-US whose product is the build's own.</summary>
    public static async Task AddGogBuild(IConnection store, ulong buildId, params (string Path, string Content)[] files)
    {
        var hashes = await HashAll(files);
        using var tx = store.BeginTransaction();
        var pathIds = AddPaths(tx, hashes);
        var manifest = new GogManifest.New(tx)
        {
            ManifestId = $"manifest-{buildId}",
            FilesIds = pathIds,
        };
        var depot = new GogDepot.New(tx)
        {
            ProductId = ProductId.From(10),
            Size = Size.From(1),
            CompressedSize = Size.From(1),
            ManifestId = manifest.Id,
            Languages = ["en-US"],
        };
        _ = new GogBuild.New(tx)
        {
            BuildId = BuildId.From(buildId),
            ProductId = ProductId.From(10),
            ManifestId = $"manifest-{buildId}",
            OperatingSystem = OperatingSystem.Windows,
            VersionName = "gog",
            Public = true,
            FilesIds = pathIds,
            DepotsIds = [depot.Id],
        };
        await tx.Commit();
    }

    /// <summary>A version of the test game that lists the manifests, which must already be in <paramref name="store"/>.</summary>
    public static async Task AddSteamVersionDefinition(IConnection store, string name, params ulong[] manifestIds)
    {
        var manifests = manifestIds
            .Select(id => SteamManifest.FindByManifestId(store.Db, ManifestId.From(id)).Single().Id)
            .ToArray();
        using var tx = store.BeginTransaction();
        _ = new VersionDefinition.New(tx)
        {
            Name = name,
            OperatingSystem = OperatingSystem.Windows,
            GameId = GameId,
            GOG = [],
            Steam = manifestIds.Select(id => id.ToString()).ToArray(),
            EpicBuildIds = [],
            SteamManifestsIds = manifests,
        };
        await tx.Commit();
    }

    /// <summary>
    /// A baseline of the installation at <paramref name="gamePath"/> holding <paramref name="files"/> as vanilla,
    /// with their hash rows, and <paramref name="nonVanilla"/> paths it found modified. Everything is in <paramref name="store"/>.
    /// </summary>
    public static async Task<EntityId> AddLocalBaseline(IConnection store, GameStore gameStore, string gamePath, LocalBaselineKind kind,
        (string Path, string Content)[] files, params string[] nonVanilla)
    {
        var hashes = await HashAll(files);
        using var tx = store.BeginTransaction();
        var pathIds = AddPaths(tx, hashes);
        var baseline = new LocalBaseline.New(tx)
        {
            Store = gameStore,
            GamePath = gamePath,
            LocatorIds = [],
            Kind = kind,
            VersionName = "",
            BuiltAt = DateTimeOffset.UnixEpoch,
            FilesIds = pathIds,
        };
        foreach (var path in nonVanilla)
        {
            _ = new NonVanillaFile.New(tx)
            {
                BaselineId = baseline.Id,
                Path = RelativePath.FromUnsanitizedInput(path),
                Reason = NonVanillaReason.HashDiffers,
                Size = Size.From(1),
            };
        }
        var result = await tx.Commit();
        return result[baseline.Id];
    }

    public static readonly NexusModsGameId GameId = NexusModsGameId.From(1234);

    /// <summary>An install of <paramref name="gamePath"/> from <paramref name="store"/> and a loadout of it carrying <paramref name="locatorIds"/>.</summary>
    public static async Task<EntityId> AddLoadout(IConnection main, GameStore store, string gamePath, params string[] locatorIds)
    {
        using var tx = main.BeginTransaction();
        var install = new GameInstallMetadata.New(tx)
        {
            Store = store,
            Path = gamePath,
            GameId = GameId,
            Name = "Test game",
        };
        var loadout = new Loadout.New(tx)
        {
            Name = "Loadout",
            ShortName = "L",
            InstallationId = install,
            Revision = 0,
            LoadoutKind = LoadoutKind.Default,
            LocatorIds = locatorIds.Select(LocatorId.From).ToArray(),
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result[loadout.Id];
    }

    private static async Task<(string Path, MultiHash Hash)[]> HashAll((string Path, string Content)[] files)
    {
        var rows = new List<(string, MultiHash)>();
        foreach (var (path, content) in files)
            rows.Add((path, await MultiHasher.HashStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)))));
        return rows.ToArray();
    }

    private static List<EntityId> AddPaths(ITransaction tx, (string Path, MultiHash Hash)[] files)
    {
        var pathIds = new List<EntityId>();
        foreach (var (path, hashes) in files)
        {
            var relation = new HashRelation.New(tx)
            {
                MinimalHash = hashes.MinimalHash,
                XxHash3 = hashes.XxHash3,
                Sha1 = hashes.Sha1,
                XxHash64 = hashes.XxHash64,
                Md5 = hashes.Md5,
                Size = hashes.Size,
                Crc32 = hashes.Crc32,
            };
            var pathRelation = new PathHashRelation.New(tx)
            {
                Path = RelativePath.FromUnsanitizedInput(path),
                HashId = relation,
            };
            pathIds.Add(pathRelation.Id);
        }

        return pathIds;
    }
}

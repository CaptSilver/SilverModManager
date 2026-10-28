using SilverModManager.Games.TestFramework.Helpers;
using OperatingSystem = SilverModManager.Abstractions.Games.FileHashes.Values.OperatingSystem;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.GOG.Values;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// Writes the rows the file_hashes queries and the service lookups read. Every method commits one
/// transaction to the connection it is given, so a row only ever refers to rows of its own store.
/// </summary>
internal static class HashRows
{
    public static async Task<EntityId> AddSteamManifest(IConnection store, ulong manifestId, params (string Path, string Content)[] files)
    {
        var hashes = await LocalBaselineRows.HashAll(files);
        using var tx = store.BeginTransaction();
        var pathIds = LocalBaselineRows.AddPaths(tx, hashes);
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
        var hashes = await LocalBaselineRows.HashAll(files);
        using var tx = store.BeginTransaction();
        var pathIds = LocalBaselineRows.AddPaths(tx, hashes);
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
    /// A baseline of the given kind that owns the manifest <paramref name="manifestId"/>, the way a build from
    /// depot manifests links them. Without <paramref name="files"/> it classified nothing.
    /// </summary>
    public static Task AddBaselineOwningManifest(IConnection store, LocalBaselineKind kind, ulong manifestId, params (string Path, string Content)[] files)
        => AddBaselineOwningManifestAt(store, "/games/test", kind, manifestId, files);

    /// <summary>As <see cref="AddBaselineOwningManifest"/>, for the install at <paramref name="gamePath"/>.</summary>
    public static async Task AddBaselineOwningManifestAt(IConnection store, string gamePath, LocalBaselineKind kind, ulong manifestId, params (string Path, string Content)[] files)
    {
        var hashes = await LocalBaselineRows.HashAll(files);
        using var tx = store.BeginTransaction();
        var pathIds = LocalBaselineRows.AddPaths(tx, hashes);
        var manifest = new SteamManifest.New(tx)
        {
            AppId = AppId.From(1u),
            DepotId = DepotId.From(2u),
            ManifestId = ManifestId.From(manifestId),
            Name = "public",
            FilesIds = pathIds,
        };
        _ = new LocalBaseline.New(tx)
        {
            Store = GameStore.Steam,
            GamePath = gamePath,
            LocatorIds = [manifestId.ToString()],
            Kind = kind,
            VersionName = "",
            BuiltAt = DateTimeOffset.UnixEpoch,
            FilesIds = pathIds,
            ManifestsIds = [manifest.Id],
        };
        await tx.Commit();
    }

    /// <summary>
    /// An <see cref="AttestedFile"/> of the baseline that owns manifest <paramref name="manifestId"/>, for a file
    /// with <paramref name="content"/> as the manifest describes it.
    /// </summary>
    public static async Task<EntityId> AddAttestedFile(IConnection store, ulong manifestId, string path, string content, DateTimeOffset? modified = null, string gamePath = "/games/test")
    {
        var hash = (await LocalBaselineRows.HashAll([(path, content)])).Single().Hash;
        var baseline = LocalBaseline.FindByGamePath(store.Db, gamePath).Single();
        var manifest = baseline.Manifests.Single(m => m.ManifestId == ManifestId.From(manifestId));
        using var tx = store.BeginTransaction();
        var row = new AttestedFile.New(tx)
        {
            BaselineId = baseline.Id,
            ManifestId = manifest.Id,
            Path = RelativePath.FromUnsanitizedInput(path),
            Sha1 = hash.Sha1,
            Size = hash.Size,
            ModifiedUtc = modified ?? DateTimeOffset.UnixEpoch,
        };
        var result = await tx.Commit();
        return result[row.Id];
    }

    /// <summary>
    /// Records that the baseline of the install at <paramref name="gamePath"/> was built for <paramref name="locatorIds"/>,
    /// the ids the loadout of the same build carries. A baseline built for other ids counts for nothing.
    /// </summary>
    public static async Task BuiltForLocatorIds(IConnection store, string gamePath, params string[] locatorIds)
    {
        var baseline = LocalBaseline.FindByGamePath(store.Db, gamePath).Single();
        using var tx = store.BeginTransaction();
        // A value retracted and added in one transaction is dropped, so only the difference is written.
        foreach (var existing in baseline.LocatorIds.Except(locatorIds)) tx.Retract(baseline.Id, LocalBaseline.LocatorIds, existing);
        foreach (var id in locatorIds.Except(baseline.LocatorIds)) tx.Add(baseline.Id, LocalBaseline.LocatorIds, id);
        await tx.Commit();
    }

    public static readonly NexusModsGameId GameId = NexusModsGameId.From(1234);

    /// <summary>The game's own id, the one installations are keyed on.</summary>
    public static readonly Sdk.Games.GameId OwnGameId = Sdk.Games.GameId.From("HashRowsGame");

    /// <summary>An install of <paramref name="gamePath"/> from <paramref name="store"/> and a loadout of it carrying <paramref name="locatorIds"/>.</summary>
    public static async Task<EntityId> AddLoadout(IConnection main, GameStore store, string gamePath, params string[] locatorIds)
    {
        using var tx = main.BeginTransaction();
        var install = new GameInstallMetadata.New(tx)
        {
            Store = store,
            Path = gamePath,
            GameId = GameId,
            Game = OwnGameId,
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
}

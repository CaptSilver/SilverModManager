using DynamicData.Kernel;
using System.Collections.Immutable;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Library.Tests.Lock;

namespace SilverModManager.Library.Tests.Inference;

/// <summary>
/// Rows the way an older version left them: library items with no destination, loadouts for named games and registry
/// entries that exist only in the test. The host database is shared by every test in the run, so each test names games
/// and Nexus ids of its own.
/// </summary>
internal sealed class InferenceFixtures(IConnection connection)
{
    private static uint _nextNexusId = 700_000;

    public static NexusModsGameId NextNexusId() => NexusModsGameId.From(Interlocked.Increment(ref _nextNexusId));

    /// <summary>A registry entry for a game that may be on Nexus Mods and, with a community, on Thunderstore.</summary>
    public static IGameData Game(GameId id, NexusModsGameId? nexusId = null, string? thunderstoreCommunity = null)
        => thunderstoreCommunity is null ? new FakeGame(id, nexusId) : new FakeThunderstoreGame(id, nexusId, thunderstoreCommunity);

    /// <summary>Only what the inferences read; the rest of a game is not needed and says so loudly.</summary>
    private class FakeGame(GameId id, NexusModsGameId? nexusId) : IGameData
    {
        public GameId GameId => id;
        public StoreIdentifiers StoreIdentifiers => new(id);
        public string DisplayName => id.ToString();
        public Optional<NexusModsGameId> NexusModsGameId => nexusId is { } nexus ? Optional<NexusModsGameId>.Create(nexus) : Optional<NexusModsGameId>.None;
        public IStreamFactory IconImage => throw new NotSupportedException();
        public IStreamFactory TileImage => throw new NotSupportedException();
        public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
        public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();
    }

    private sealed class FakeThunderstoreGame(GameId id, NexusModsGameId? nexusId, string community) : FakeGame(id, nexusId), IThunderstoreGame
    {
        public string ThunderstoreCommunity => community;
        public string ThunderstoreLoaderPackage => throw new NotSupportedException();
        public IReadOnlyList<string> ThunderstoreLoaderAliases => [];
    }

    public static GameId UniqueGame(string prefix) => GameId.From($"{prefix}-{Guid.NewGuid():N}");

    /// <summary>A loadout over an installation of <paramref name="game"/>, or over one with no game recorded.</summary>
    public async Task<Loadout.ReadOnly> CreateLoadout(GameId? game, NexusModsGameId? nexusId = null)
    {
        using var tx = connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/tmp/inference-tests-" + Guid.NewGuid(),
            Name = "Inference Test Game",
            Store = GameStore.Unknown,
        };
        if (game is { } id) tx.Add(metadata.Id, GameInstallMetadata.Game, id);
        if (nexusId is { } nexus) tx.Add(metadata.Id, GameInstallMetadata.GameId, nexus);
        var loadout = new Loadout.New(tx)
        {
            Name = "Inference " + Guid.NewGuid(),
            ShortName = "IN",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }

    /// <summary>A library item with no destination, as one added before destinations were recorded.</summary>
    public async Task<LibraryItem.ReadOnly> AddLegacyItem(Action<ITransaction, EntityId>? extend = null)
    {
        var fixtures = new LockFixtures(connection);
        var file = await fixtures.AddLibraryFile($"{Guid.NewGuid():N}.zip", Guid.NewGuid().ToString(), sha256: null,
            extend is null ? null : (tx, newFile) => extend(tx, newFile.Id));
        return LibraryItem.Load(connection.Db, file.Id);
    }

    public async Task Install(Loadout.ReadOnly loadout, LibraryItem.ReadOnly item)
    {
        var fixtures = new LockFixtures(connection);
        await fixtures.Link(loadout, LibraryFile.Load(connection.Db, item.Id), "mod " + Guid.NewGuid());
    }
}

using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// A game that exists only in one test. The test database is shared by every test in the host, so a test that wrote
/// library items for a real game would change what other tests count for it; a fresh pair has a slice nothing else wrote to.
/// </summary>
internal sealed class TestGame(GameId gameId, NexusModsGameId nexusId) : IGameData
{
    private static int _nextNexusId = 960_000;

    public GameId GameId { get; } = gameId;
    public Optional<NexusModsGameId> NexusModsGameId => Optional.Some(nexusId);
    public StoreIdentifiers StoreIdentifiers => new(GameId);
    public string DisplayName => "Test game " + GameId;
    public IStreamFactory IconImage => throw new NotSupportedException();
    public IStreamFactory TileImage => throw new NotSupportedException();
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
    public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();

    /// <summary>Two games that share a Nexus id the way Skyrim SE and Skyrim VR do.</summary>
    public static (TestGame Game, TestGame Sibling) NewPair()
    {
        var nexus = SilverModManager.Sdk.NexusModsApi.NexusModsGameId.From((uint)Interlocked.Increment(ref _nextNexusId));
        return (new TestGame(SilverModManager.Sdk.Games.GameId.From("game-" + Guid.NewGuid().ToString("N")), nexus),
            new TestGame(SilverModManager.Sdk.Games.GameId.From("sibling-" + Guid.NewGuid().ToString("N")), nexus));
    }

    /// <summary>A loadout for <paramref name="game"/> on a folder that does not exist.</summary>
    public static async Task<LoadoutId> AddLoadout(IConnection connection, IGameData game)
    {
        using var tx = connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/nonexistent/" + Guid.NewGuid().ToString("N"),
            Name = game.DisplayName,
            Store = GameStore.Unknown,
            Game = game.GameId,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = "Destination loadout",
            ShortName = "DST",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout).LoadoutId;
    }
}

using System.Collections.Immutable;
using System.Text;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// A generated file reflects the loadout one Synchronize after the loadout changes, on every backend, and
/// what the app wrote is never read back into the loadout as if the user had edited it.
/// </summary>
public class IntrinsicFileSyncTests(ITestOutputHelper helper) : AIsolatedGameTest<IntrinsicFileSyncTests, ModListingStubbedGame>(helper)
{
    private AbsolutePath Listing => GameInstallation.Locations.ToAbsolutePath(ModListingStubbedGame.ListingPath);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        ModListingStubbedGame.ResetIngested();
        return base.AddServices(services)
            .AddGame<ModListingStubbedGame>()
            .AddUniversalGameLocator<ModListingStubbedGame>(Version.Parse("0.0.0"));
    }

    private async Task<Loadout.ReadOnly> CreateLoadoutOn(string backend)
    {
        var loadout = await CreateLoadout();
        if (backend == "copy")
            await SynchronizerService.ChangeDeploymentBackend(GameInstallation, new GamePath(LocationId.Game, ""), DeploymentBackendId.Copy, chosen: true);
        return loadout;
    }

    private async Task<LoadoutItemGroupId> AddMod(Loadout.ReadOnly loadout, string fileName)
    {
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, [(RelativePath)fileName], loadout.LoadoutId, fileName);
        await tx.Commit();

        return LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId)
            .OfTypeLoadoutItemGroup()
            .Single(group => group.AsLoadoutItem().Name == fileName)
            .LoadoutItemGroupId;
    }

    private async Task Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    private async Task<string[]> Listed() =>
        Listing.FileExists ? (await Listing.ReadAllTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];

    [Theory]
    [InlineData("default")]
    [InlineData("copy")]
    public async Task ASecondSyncWithNoChangeDoesNotIngestTheGeneratedFile(string backend)
    {
        var loadout = await CreateLoadoutOn(backend);
        await AddMod(loadout, "a.mod");
        await Apply(loadout);
        await Apply(loadout);
        var ingestedAfterWrite = ModListingStubbedGame.Ingested;

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);

        var actions = tree[ModListingStubbedGame.ListingPath].Actions;
        actions.Should().NotHaveFlag(Actions.IngestFromDisk);
        actions.Should().NotHaveFlag(Actions.AdaptLoadout);
        actions.Should().NotHaveFlag(Actions.BackupFile);

        await Apply(loadout);
        ModListingStubbedGame.Ingested.Should().Be(ingestedAfterWrite, "the file on disk is what the last sync wrote");
        (await Listed()).Should().Equal("a.mod");
    }

    [Theory]
    [InlineData("default")]
    [InlineData("copy")]
    public async Task AChangeAfterAWriteReachesTheFileOnTheNextSync(string backend)
    {
        var loadout = await CreateLoadoutOn(backend);
        var first = await AddMod(loadout, "a.mod");
        await Apply(loadout);
        (await Listed()).Should().Equal("a.mod");

        var second = await AddMod(loadout, "b.mod");
        await Apply(loadout);
        (await Listed()).Should().Equal("a.mod", "b.mod");

        await LoadoutManager.RemoveItems([first]);
        await Apply(loadout);
        (await Listed()).Should().Equal("b.mod");

        await LoadoutManager.RemoveItems([second]);
        await Apply(loadout);
        (await Listed()).Should().BeEmpty();
    }
}

/// <summary>
/// A stubbed game whose synchronizer writes a file naming every loadout file at the game folder's top level.
/// </summary>
public class ModListingStubbedGame : IGame, IGameData<ModListingStubbedGame>
{
    public static GameId GameId { get; } = GameId.From("ModListingStubbedGame");
    public static string DisplayName => "Stubbed Game With A Mod Listing";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(uint.MaxValue - 2);

    public static readonly GamePath ListingPath = new(LocationId.Preferences, "mods.cfg");

    /// <summary>How many times the synchronizer asked the listing to read a file back from disk.</summary>
    public static int Ingested => _ingested;
    private static int _ingested;

    public static void ResetIngested() => _ingested = 0;

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [45u],
        GOGProductIds = [45L],
        EGSCatalogItemId = ["epic-game-id-mod-listing"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<NexusMods.StandardGameLocators.TestHelpers.StubbedGames.StubbedGame>("NexusMods.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public ModListingStubbedGame(IServiceProvider provider) => _serviceProvider = provider;

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new ListingSynchronizer(_serviceProvider);

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        return new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path.Combine("game") },
            { LocationId.Preferences, gameLocatorResult.Path.Combine("preferences") },
        }.ToImmutableDictionary();
    }

    public ILibraryItemInstaller[] LibraryItemInstallers => [];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);

    private sealed class ListingSynchronizer(IServiceProvider provider) : DefaultSynchronizer(provider)
    {
        public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) => new() { [ListingPath] = new Listing() };
    }

    private sealed class Listing : IIntrinsicFile
    {
        public GamePath Path => ListingPath;

        public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
        {
            var names = syncTree
                .Where(entry => entry.Key.LocationId == LocationId.Game && entry.Value.HaveLoadout)
                .Select(entry => entry.Key.FileName.ToString())
                .Order(StringComparer.Ordinal);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(names.Select(name => name + "\n"))));
        }

        public Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
        {
            Interlocked.Increment(ref _ingested);
            return Task.CompletedTask;
        }
    }
}

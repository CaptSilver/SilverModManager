using System.Collections.Immutable;
using System.Reactive.Linq;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
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
/// The hook a game uses to bring a loadout up to date runs before an Apply and before a loadout switch builds
/// its tree, and never from the read-only paths that build a tree for diagnostics.
/// </summary>
public class PrepareLoadoutForSyncTests : AIsolatedGameTest<PrepareLoadoutForSyncTests, PrepareStubbedGame>
{
    private const string MarkerFile = "marker.txt";

    public PrepareLoadoutForSyncTests(ITestOutputHelper helper) : base(helper) => PrepareStubbedGame.Reset();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<PrepareStubbedGame>()
            .AddUniversalGameLocator<PrepareStubbedGame>(Version.Parse("0.0.0"));
    }

    private AbsolutePath MarkerOnDisk => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, MarkerFile));

    // Commits a mod to the loadout the hook was given, the way a game seeding its own data would.
    private void CommitMarkerFromTheHook()
    {
        PrepareStubbedGame.Prepare = async loadout =>
        {
            using var tx = Connection.BeginTransaction();
            await AddModAsync(tx, [(RelativePath)MarkerFile], loadout.LoadoutId, "marker");
            await tx.Commit();
            return Refresh(loadout);
        };
    }

    [Fact]
    public async Task ApplyCallsTheHookOnceAndDeploysWhatItCommitted()
    {
        var loadout = await CreateLoadout();
        PrepareStubbedGame.Calls.Clear();
        CommitMarkerFromTheHook();

        await Synchronizer.Synchronize(loadout);

        PrepareStubbedGame.Calls.Should().Equal(loadout.LoadoutId);
        MarkerOnDisk.FileExists.Should().BeTrue("the hook ran before the tree was built, so its commit is deployed");
    }

    [Fact]
    public async Task SwitchingLoadoutsCallsTheHookForTheTargetLoadout()
    {
        var loadoutA = await CreateLoadout();
        await Synchronizer.Synchronize(loadoutA);
        var loadoutB = await CreateLoadout();
        PrepareStubbedGame.Calls.Clear();
        CommitMarkerFromTheHook();

        await Synchronizer.Synchronize(Refresh(loadoutB));

        // Deactivating A syncs it first, so A may be prepared too; B is prepared last, before its tree is built.
        PrepareStubbedGame.Calls.Should().EndWith(loadoutB.LoadoutId);
        MarkerOnDisk.FileExists.Should().BeTrue("the switched-to loadout was prepared before its tree was built");
    }

    [Fact]
    public async Task TheDiagnosticsPathsNeverCallTheHook()
    {
        var loadout = await CreateLoadout();
        PrepareStubbedGame.Calls.Clear();
        CommitMarkerFromTheHook();
        var txBefore = Connection.Db.BasisTxId;

        await Synchronizer.BuildSyncTree(loadout);
        await DiagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

        PrepareStubbedGame.Calls.Should().BeEmpty();
        Connection.Db.BasisTxId.Should().Be(txBefore, "nothing was committed");
    }
}

/// <summary>
/// A stubbed game whose synchronizer records every loadout handed to the preparation hook and runs a delegate a test sets.
/// </summary>
public class PrepareStubbedGame : IGame, IGameData<PrepareStubbedGame>
{
    public static GameId GameId { get; } = GameId.From("PrepareStubbedGame");
    public static string DisplayName => "Stubbed Game With A Preparation Hook";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(uint.MaxValue - 4);

    public static List<LoadoutId> Calls { get; private set; } = [];
    public static Func<Loadout.ReadOnly, Task<Loadout.ReadOnly>>? Prepare { get; set; }

    public static void Reset()
    {
        Calls = [];
        Prepare = null;
    }

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [47u],
        GOGProductIds = [47L],
        EGSCatalogItemId = ["epic-game-id-prepare-hook"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<NexusMods.StandardGameLocators.TestHelpers.StubbedGames.StubbedGame>("NexusMods.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public PrepareStubbedGame(IServiceProvider provider) => _serviceProvider = provider;

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new PrepareSynchronizer(_serviceProvider);

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        return new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path.Combine("game") },
        }.ToImmutableDictionary();
    }

    public ILibraryItemInstaller[] LibraryItemInstallers => [];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);

    private sealed class PrepareSynchronizer(IServiceProvider provider) : DefaultSynchronizer(provider)
    {
        protected override async ValueTask<Loadout.ReadOnly> PrepareLoadoutForSync(Loadout.ReadOnly loadout)
        {
            Calls.Add(loadout.LoadoutId);
            return Prepare is null ? loadout : await Prepare(loadout);
        }
    }
}

using System.Collections.Immutable;
using System.ComponentModel;
using DynamicData;
using OneOf;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Sdk.Games;
using SilverModManager.Games.TestFramework;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// The shared game base builds everything from its overridable factories on first use, once, and not in its constructor.
/// </summary>
public class AGameTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<AGameTests>(helper)
{
    [Fact]
    public void Constructing_RunsNoVirtualFactory()
    {
        var game = new FakeGame(ServiceProvider);

        game.InstallersBuilt.Should().Be(0);
        game.SynchronizerBuilt.Should().Be(0);
        game.VarietiesBuilt.Should().Be(0);
    }

    [Fact]
    public void LibraryItemInstallers_IsBuiltOnceAndCached()
    {
        var game = new FakeGame(ServiceProvider);

        var first = game.LibraryItemInstallers;
        var second = game.LibraryItemInstallers;

        game.InstallersBuilt.Should().Be(1);
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void SortOrderManager_RegistersTheGamesVarieties()
    {
        var game = new FakeGame(ServiceProvider);

        using var manager = (SortOrderManager)game.SortOrderManager;

        manager.GetSortOrderVarieties().Should().Equal(FakeGame.Varieties);
        manager.GetSortOrderVarieties()[0].Should().BeSameAs(FakeGame.Varieties[0]);
        game.VarietiesBuilt.Should().Be(1);
    }

    [Fact]
    public void DiagnosticEmitters_DefaultToTheBaselineThree()
    {
        var game = new FakeGame(ServiceProvider);

        game.DiagnosticEmitters.Select(e => e.GetType()).Should().Equal(
            typeof(NoWayToSourceFilesOnDisk),
            typeof(UndeployableLoadoutDueToMissingGameFiles),
            typeof(ProtonPrefixEmitter));
    }

    [Fact]
    public void GetPrimaryFile_IsThePrimaryExeInTheGameLocation()
    {
        var game = new FakeGame(ServiceProvider);

        game.GetPrimaryFile(Installation(new TemporaryFileManager(FileSystem).CreateFolder().Path))
            .Should().Be(new GamePath(LocationId.Game, "bin/fake.exe"));
    }

    [Fact]
    public void GetLocalVersion_IsNoneWhenTheExeIsMissing()
    {
        var game = new FakeGame(ServiceProvider);
        using var temp = new TemporaryFileManager(FileSystem);

        game.GetLocalVersion(Installation(temp.CreateFolder().Path)).HasValue.Should().BeFalse();
    }

    // Only Locations is read by the game under test.
    private static GameInstallation Installation(AbsolutePath root) =>
        new(null!, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, root)));

    // A real instance: reading a substitute's struct-typed id property would build the default id, which the id type refuses.
    private static ISortOrderVariety Variety() => new SortOrderVarietyStub();

    private sealed class SortOrderVarietyStub : ISortOrderVariety
    {
        public SortOrderVarietyId SortOrderVarietyId { get; } = SortOrderVarietyId.From(Guid.NewGuid());
        public ListSortDirection SortDirectionDefault => ListSortDirection.Ascending;
        public IndexOverrideBehavior IndexOverrideBehavior => throw new NotSupportedException();
        public SortOrderUiMetadata SortOrderUiMetadata => throw new NotSupportedException();
        public Optional<SortOrderId> GetSortOrderIdFor(OneOf<LoadoutId, CollectionGroupId> parentEntity, IDb? db = null) => throw new NotSupportedException();
        public ValueTask<SortOrderId> GetOrCreateSortOrderFor(LoadoutId loadoutId, OneOf<LoadoutId, CollectionGroupId> parentEntity, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask ReconcileSortOrder(SortOrderId sortOrderId, IDb? db = null, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask DeleteSortOrder(SortOrderId sortOrderId, CancellationToken token = default) => ValueTask.CompletedTask;
        public IObservable<IChangeSet<IReactiveSortItem, ISortItemKey>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId) => throw new NotSupportedException();
        public IReadOnlyList<IReactiveSortItem> GetSortOrderItems(SortOrderId sortOrderId, IDb? db = null) => throw new NotSupportedException();
        public Task MoveItems(SortOrderId sortOrderId, ISortItemKey[] itemsToMove, ISortItemKey dropTargetItem, TargetRelativePosition relativePosition, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task MoveItemDelta(SortOrderId sortOrderId, ISortItemKey sourceItem, int delta, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class FakeGame(IServiceProvider provider) : AGame<FakeGame>(provider), IGameData<FakeGame>
    {
        public static GameId GameId { get; } = GameId.From("Tests.FakeGame");
        public static string DisplayName => "Fake Game";
        public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Optional<Sdk.NexusModsApi.NexusModsGameId>.None;

        public static ISortOrderVariety[] Varieties { get; } = [Variety()];

        public int InstallersBuilt { get; private set; }
        public int SynchronizerBuilt { get; private set; }
        public int VarietiesBuilt { get; private set; }

        public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId) { SteamAppIds = [1u] };
        protected override RelativePath PrimaryExe => "bin/fake.exe";

        protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider)
        {
            SynchronizerBuilt++;
            throw new NotSupportedException();
        }

        protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider)
        {
            InstallersBuilt++;
            return [];
        }

        protected override ISortOrderVariety[] CreateSortOrderVarieties(IServiceProvider provider)
        {
            VarietiesBuilt++;
            return Varieties;
        }

        public override ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) =>
            ImmutableDictionary<LocationId, AbsolutePath>.Empty;
    }
}

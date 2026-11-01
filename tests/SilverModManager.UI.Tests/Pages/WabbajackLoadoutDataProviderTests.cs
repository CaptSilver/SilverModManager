using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.Wabbajack.Models;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// A modlist install writes each mod as a plain group under the modlist's group, with no library link. My Mods lists
/// those groups, and the loadout's All count includes them.
/// </summary>
public class WabbajackLoadoutDataProviderTests(IServiceProvider provider) : AUiTest(provider)
{
    private WabbajackLoadoutDataProvider CreateProvider() => new(Provider);

    private static LoadoutFilter AllFor(Loadout.ReadOnly loadout) => new()
    {
        LoadoutId = loadout.LoadoutId,
        CollectionGroupId = Optional<LoadoutItemGroupId>.None,
    };

    private static LoadoutFilter CollectionFilter(Loadout.ReadOnly loadout, EntityId group) => new()
    {
        LoadoutId = loadout.LoadoutId,
        CollectionGroupId = LoadoutItemGroupId.From(group),
    };

    private async Task<EntityId> AddList(string name)
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = $"{name}.wabbajack",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = $"{name}.wabbajack" },
        };
        _ = new WabbajackModlistLibraryFile.New(tx, id)
        {
            LibraryFile = file, Name = name, Author = "Someone", Version = "1", GameType = "NoSuchGame",
            ArchiveCount = 0, ArchiveSize = Size.From(1), InstallSize = Size.From(1), IsNsfw = false,
        };
        return (await tx.Commit()).Remap(file).Id;
    }

    private async Task<EntityId> AddModlistGroup(Loadout.ReadOnly loadout, string name) =>
        await WabbajackGroupSeed.AddGroup(Connection, await AddList(name), loadout.LoadoutId, name);

    /// <summary>What the install job writes for one mod: a plain group under the modlist group, holding a file.</summary>
    private async Task<EntityId> AddMod(Loadout.ReadOnly loadout, EntityId parent, string name, bool withFile = true)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, Name = name, ParentId = parent },
        };
        if (withFile)
        {
            _ = new LoadoutFile.New(tx, out var fileId)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
                {
                    LoadoutItem = new LoadoutItem.New(tx, fileId) { LoadoutId = loadout.LoadoutId, Name = name + ".esp", ParentId = id },
                    TargetPath = new GamePath(LocationId.Game, name + ".esp").ToGamePathParentTuple(loadout.LoadoutId),
                },
                Hash = Hash.From(0xAB),
                Size = Size.From(10),
            };
        }
        return (await tx.Commit()).Remap(group).Id;
    }

    private async Task LinkToLibrary(EntityId group)
    {
        using var tx = Connection.BeginTransaction();
        var item = new LibraryItem.New(tx) { Name = "linked" };
        tx.Add(group, LibraryLinkedLoadoutItem.LibraryItemId, item.Id);
        await tx.Commit();
    }

    private async Task<IObservableCache<CompositeItemModel<EntityId>, EntityId>> Observe(LoadoutFilter filter) =>
        await OnUi(() => Task.FromResult(CreateProvider().ObserveLoadoutItems(filter).AsObservableCache()));

    private CountWatch ObserveCount(LoadoutFilter filter) => new(CreateProvider().CountLoadoutItems(filter));

    private sealed class CountWatch : IDisposable
    {
        private readonly IDisposable _subscription;
        public CountWatch(IObservable<int> counts) => _subscription = counts.Subscribe(count => Latest = count);
        public int Latest { get; private set; } = -1;
        public void Dispose() => _subscription.Dispose();
    }

    private static string NameOf(CompositeItemModel<EntityId> row) =>
        row.Get<NameComponent>(SharedColumns.Name.NameComponentKey).Value.Value;

    [Fact]
    public async Task ACollectionFilterForAModlistGroupListsItsChildGroupsAsRows()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "wj-rows-" + Guid.NewGuid().ToString("N"));
        var list = await AddModlistGroup(loadout, "Yggdrasil");
        var skyui = await AddMod(loadout, list, "SkyUI");
        var ussep = await AddMod(loadout, list, "USSEP");

        using var rows = await Observe(CollectionFilter(loadout, list));

        await Eventually(() => rows.Keys.Should().BeEquivalentTo([skyui, ussep]));
        var row = rows.Lookup(skyui).Value;
        NameOf(row).Should().Be("SkyUI");
        row.Get<StringComponent>(SharedColumns.Source.ComponentKey).Value.Value.Should().Be(ModSources.Wabbajack);
        row.Get<StringComponent>(LoadoutColumns.Collections.ComponentKey).Value.Value.Should().Be("Yggdrasil");
        row.Get<LoadoutComponents.EnabledStateToggle>(LoadoutColumns.EnabledState.EnabledStateToggleComponentKey).Should().NotBeNull();
        row.Get<LoadoutComponents.LoadoutItemIds>(LoadoutColumns.EnabledState.LoadoutItemIdsComponentKey).Should().NotBeNull();
    }

    [Fact]
    public async Task TheFilesOfAModAreNotRowsOfTheirOwn()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "wj-files-" + Guid.NewGuid().ToString("N"));
        var list = await AddModlistGroup(loadout, "Files");
        var mod = await AddMod(loadout, list, "Mod", withFile: true);

        using var rows = await Observe(CollectionFilter(loadout, list));

        await Eventually(() => rows.Keys.Should().BeEquivalentTo([mod]));
    }

    [Fact]
    public async Task GroupsTheInstallAddsAfterTheViewIsOpenAppearAsRows()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "wj-live-" + Guid.NewGuid().ToString("N"));
        var list = await AddModlistGroup(loadout, "Live");
        using var rows = await Observe(CollectionFilter(loadout, list));
        using var all = await Observe(AllFor(loadout));
        rows.Count.Should().Be(0);

        var first = await AddMod(loadout, list, "First");
        await Eventually(() => rows.Keys.Should().BeEquivalentTo([first]));
        var second = await AddMod(loadout, list, "Second");

        await Eventually(() => rows.Keys.Should().BeEquivalentTo([first, second]));
        await Eventually(() => all.Keys.Should().BeEquivalentTo([first, second]));
    }

    [Fact]
    public async Task WithoutACollectionFilterEveryModlistGroupOfTheLoadoutContributesItsChildren()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "wj-all-" + Guid.NewGuid().ToString("N"));
        var otherLoadout = await SeedLoadoutRow(GameStore.Steam, "wj-other-" + Guid.NewGuid().ToString("N"));
        var listA = await AddModlistGroup(loadout, "A");
        var listB = await AddModlistGroup(loadout, "B");
        var elsewhere = await AddModlistGroup(otherLoadout, "C");
        var a1 = await AddMod(loadout, listA, "A1");
        var b1 = await AddMod(loadout, listB, "B1");
        _ = await AddMod(otherLoadout, elsewhere, "C1");

        using var rows = await Observe(AllFor(loadout));

        await Eventually(() => rows.Keys.Should().BeEquivalentTo([a1, b1]));
        rows.Lookup(a1).Value.Get<StringComponent>(LoadoutColumns.Collections.ComponentKey).Value.Value.Should().Be("A");
        rows.Lookup(b1).Value.Get<StringComponent>(LoadoutColumns.Collections.ComponentKey).Value.Value.Should().Be("B");
    }

    [Fact]
    public async Task TheCountGrowsAsTheInstallAddsGroups()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "wj-count-" + Guid.NewGuid().ToString("N"));
        var list = await AddModlistGroup(loadout, "Counted");
        using var count = ObserveCount(AllFor(loadout));
        await Eventually(() => count.Latest.Should().Be(0));

        await AddMod(loadout, list, "One");
        await AddMod(loadout, list, "Two");
        await Eventually(() => count.Latest.Should().Be(2));

        await AddMod(loadout, list, "Three", withFile: false);
        await Eventually(() => count.Latest.Should().Be(3));
    }

    [Fact]
    public async Task TheGameSettingsGroupOfAModlistIsARowLikeTheModsAreAndKeepsItsName()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "wj-settings-" + Guid.NewGuid().ToString("N"));
        var list = await AddModlistGroup(loadout, "Settings");
        var settings = await AddMod(loadout, list, "Game settings");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(settings, LoadoutItemGroup.GameSettingsGroup, Null.Instance);
            await tx.Commit();
        }

        using var rows = await Observe(CollectionFilter(loadout, list));

        await Eventually(() => rows.Keys.Should().BeEquivalentTo([settings]));
        NameOf(rows.Lookup(settings).Value).Should().Be("Game settings");
    }

    [Fact]
    public async Task TheLoadoutsAllCountIncludesAModlistsGroups()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "wj-total-" + Guid.NewGuid().ToString("N"));
        var list = await AddModlistGroup(loadout, "Total");
        await AddMod(loadout, list, "One");
        await AddMod(loadout, list, "Two");

        var count = 0;
        using var subscription = LoadoutDataProviderHelper.CountAllLoadoutItems(Provider, loadout.LoadoutId).Subscribe(c => count = c);

        await Eventually(() => count.Should().Be(2));
    }

    [Fact]
    public async Task ANexusCollectionPageIsUnchanged()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "wj-nexus-" + Guid.NewGuid().ToString("N"));
        EntityId collection;
        using (var tx = Connection.BeginTransaction())
        {
            var group = new CollectionGroup.New(tx, out var id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Nexus collection", LoadoutId = loadout.LoadoutId },
                },
            };
            collection = (await tx.Commit()).Remap(group).Id;
        }
        await AddMod(loadout, collection, "Child of a Nexus collection");

        using var rows = await Observe(CollectionFilter(loadout, collection));
        using var count = ObserveCount(CollectionFilter(loadout, collection));
        using var all = await Observe(AllFor(loadout));
        using var allCount = ObserveCount(AllFor(loadout));

        await Task.Delay(300);
        rows.Count.Should().Be(0);
        count.Latest.Should().Be(0);
        all.Count.Should().Be(0);
        allCount.Latest.Should().Be(0);
    }

    [Fact]
    public async Task ALibraryLinkedChildIsLeftToTheProviderThatOwnsItSoItIsNotCountedTwice()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "wj-linked-" + Guid.NewGuid().ToString("N"));
        var list = await AddModlistGroup(loadout, "Linked");
        var plain = await AddMod(loadout, list, "Plain");
        var linked = await AddMod(loadout, list, "Linked");
        await LinkToLibrary(linked);

        using var rows = await Observe(AllFor(loadout));
        using var count = ObserveCount(AllFor(loadout));

        await Eventually(() => rows.Keys.Should().BeEquivalentTo([plain]));
        await Eventually(() => count.Latest.Should().Be(1));
    }

    [Fact]
    public async Task ARowIsRegisteredAmongTheLoadoutProviders()
    {
        Provider.GetServices<ILoadoutDataProvider>().Should().ContainSingle(p => p is WabbajackLoadoutDataProvider);
    }
}

using System.ComponentModel;
using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Models;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using LoadoutSortOrder = SilverModManager.Abstractions.Loadouts.SortOrder;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.SortOrder;

/// <summary>
/// The Witcher 3 Load Order page: a view over the loadout's group priorities, one row per Mods folder, with
/// mod0000_MergedFiles locked as the folder the game reads first. Indexes are the order mods.settings is written in
/// (index 0 wins); the page lists them in reverse, so the winner is the bottom row.
/// </summary>
public class WitcherModSortOrderVarietyTests(ITestOutputHelper helper)
    : AIsolatedGameTest<WitcherModSortOrderVarietyTests, Witcher3Game>(helper)
{
    private const string Merged = "mod0000_MergedFiles";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    private WitcherModSortOrderVariety NewVariety() => new(ServiceProvider, Game);

    private IDb Db => Connection.Db;

    private static SortItemKey<string> Key(string folder) => new(folder.ToLowerInvariant());

    private static GamePath ModsPath(string path) => new(LocationId.Game, path);

    private async Task<(Loadout.ReadOnly Loadout, WitcherModSortOrderVariety Variety, SortOrderId SortOrder)> Reconciled()
    {
        var loadout = await CreateLoadout();
        var variety = NewVariety();
        var id = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        return (loadout, variety, id);
    }

    /// <summary>A mod group holding the given files, with a priority row of <paramref name="priority"/>.</summary>
    private async Task<LoadoutItemGroupId> AddMod(Loadout.ReadOnly loadout, string name, ulong priority, params string[] paths)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        foreach (var path in paths) AddFile(tx, loadout.LoadoutId, group, ModsPath(path), path);
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From(priority) };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group.Value]);
    }

    private ulong PriorityOf(LoadoutItemGroupId group) => LoadoutItemGroupPriority.FindByTarget(Db, group).Single().Priority.Value;

    private static string[] Folders(IEnumerable<IReactiveSortItem> items) => items.Select(i => i.DisplayName).ToArray();

    private static string[] Order(IWitcherModSortOrderVariety variety, Loadout.ReadOnly loadout) =>
        variety.StoredOrder(loadout).Select(e => e.Folder).ToArray();

    private List<(string Folder, bool IsEnabled, int SortIndex)> StoredRows(SortOrderId id) =>
        SortOrderItem.FindByParentSortOrder(Db, id)
            .Select(item => (item, folder: WitcherModSortOrderItem.Load(Db, item.Id)))
            .Select(t => (t.folder.FolderName.Value, t.folder.IsEnabled.Value, t.item.SortIndex))
            .Order().ToList();

    private async Task<LoadoutId> SeedLoadout(GameId game)
    {
        using var tx = Connection.BeginTransaction();
        var install = new GameInstallMetadata.New(tx)
        {
            Store = GameStore.Unknown,
            Path = "/games/" + Guid.NewGuid(),
            GameId = Witcher3Game.NexusModsGameId.Value,
            Name = game.ToString(),
            Game = game,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = Guid.NewGuid().ToString(),
            ShortName = "ZZZ",
            InstallationId = install,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return LoadoutId.From(result.Remap(loadout).Id);
    }

    private Loadout.ReadOnly Fresh(Loadout.ReadOnly loadout) => Loadout.Load(Db, loadout.LoadoutId);

    [Fact]
    public async Task GetOrCreate_CalledFromSeveralPlacesAtOnce_MakesOneSortOrder()
    {
        // The load-order manager, the synchronizer and the page each ask through their own variety instance.
        // A loadout of another game: this game's load-order manager never creates an order for it behind the test's back.
        var loadoutId = await SeedLoadout(GameId.From("Test.AnotherGame"));
        var varieties = Enumerable.Range(0, 16).Select(_ => NewVariety()).ToArray();
        using var start = new ManualResetEventSlim();

        var asks = varieties
            .Select(variety => Task.Run(async () =>
            {
                start.Wait();
                return await variety.GetOrCreateSortOrderFor(loadoutId, loadoutId);
            }))
            .ToArray();
        start.Set();
        var ids = await Task.WhenAll(asks);

        ids.Distinct().Should().ContainSingle("every caller gets the one order");
        LoadoutSortOrder.FindByParentEntity(Db, loadoutId)
            .Where(order => order.SortOrderTypeId == varieties[0].SortOrderVarietyId.Value)
            .Should().ContainSingle("a second order would shadow the first and survive a delete of it");
    }

    [Fact]
    public async Task TheMergedFolder_IsAlwaysRowZeroAndLocked()
    {
        var (_, variety, id) = await Reconciled();

        var row = variety.GetSortOrderItems(id, Db).Single();

        row.Key.Key.Should().Be("mod0000_mergedfiles");
        row.SortIndex.Should().Be(0);
        var details = (ISortItemDetails)row;
        details.IsLocked.Should().BeTrue();
        details.LockReason.Should().Be(SortItemLockReason.AlwaysLoaded);
        details.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Folders_AreOrderedByTheirGroupsHighestPriority()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        await AddMod(loadout, "B", 2, "Mods/modB/b.ws");

        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modB", "modA");
        variety.GetSortOrderItems(id, Db).Select(i => i.SortIndex).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task SmallerIndexWins()
    {
        var (_, variety, _) = await Reconciled();

        variety.IndexOverrideBehavior.Should().Be(IndexOverrideBehavior.SmallerIndexWins);
    }

    [Fact]
    public async Task Page_ShowsTheWinnerAtTheBottom()
    {
        var (_, variety, _) = await Reconciled();

        variety.SortDirectionDefault.Should().Be(ListSortDirection.Descending);
        LoadOrderConvention.WinnerOnTop(variety.SortDirectionDefault, variety.IndexOverrideBehavior).Should().BeFalse();
        variety.SortOrderUiMetadata.SortOrderName.Should().Be("Load Order");
        variety.SortOrderUiMetadata.OverrideInfoTitle.Should().Be(LoadOrderConvention.Title("Load Order"));
        variety.SortOrderUiMetadata.IndexColumnHeader.Should().Be("Load Order");
        variety.SortOrderUiMetadata.WinnerIndexToolTip.Should().Be(LoadOrderConvention.WinnerToolTip("a mod lower in the list overrides the ones above it."));
    }

    [Fact]
    public async Task FoldersDifferingOnlyByCase_ShareOneRow()
    {
        var (loadout, variety, id) = await Reconciled();
        var first = await AddMod(loadout, "First", 1, "Mods/modSDL/a.ws");
        var second = await AddMod(loadout, "Second", 2, "Mods/ModSdl/b.ws");

        var rows = variety.GetSortOrderItems(id, Db);

        Folders(rows).Should().Equal(Merged, "ModSdl");
        var data = (WitcherModLoadoutData)rows[1].LoadoutData!;
        data.Groups.Should().Equal(second, first);
        data.ModGroupId.Value.Should().Be(second);
        rows[1].ModName.Should().Be("Second");
    }

    [Fact]
    public async Task DlcFolders_AreNotListed()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Dlc", 1, "dlc/dlcX/content/a.bundle");

        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged);
    }

    [Fact]
    public async Task AGroupWithoutModsFiles_HasNoRow()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Menu", 1, "bin/config/r4game/user_config_matrix/pc/x.xml");

        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged);
    }

    [Fact]
    public async Task MovingAFolderBeforeAnother_RaisesItsGroupsPriority()
    {
        var (loadout, variety, id) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        var b = await AddMod(loadout, "B", 2, "Mods/modB/b.ws");

        await variety.MoveItems(id, [Key("modA")], Key("modB"), TargetRelativePosition.BeforeTarget, Db);

        PriorityOf(a).Should().BeGreaterThan(PriorityOf(b));
        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modA", "modB");
    }

    [Fact]
    public async Task MovingAFolderToTheLastIndex_LowersItsGroupsPriority()
    {
        var (loadout, variety, id) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        var b = await AddMod(loadout, "B", 2, "Mods/modB/b.ws");
        await variety.MoveItems(id, [Key("modA")], Key("modB"), TargetRelativePosition.BeforeTarget, Db);

        await variety.MoveItems(id, [Key("modA")], Key("modB"), TargetRelativePosition.AfterTarget, Db);

        PriorityOf(a).Should().BeLessThan(PriorityOf(b));
        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modB", "modA");
    }

    [Fact]
    public async Task MovingAFolderWithTwoGroups_MovesBothGroups()
    {
        var (loadout, variety, id) = await Reconciled();
        var sdlLow = await AddMod(loadout, "SdlLow", 1, "Mods/modSDL/a.ws");
        var sdlHigh = await AddMod(loadout, "SdlHigh", 2, "Mods/ModSdl/b.ws");
        var other = await AddMod(loadout, "Other", 3, "Mods/modB/b.ws");

        await variety.MoveItems(id, [Key("modsdl")], Key("modB"), TargetRelativePosition.BeforeTarget, Db);

        PriorityOf(sdlLow).Should().BeGreaterThan(PriorityOf(other));
        PriorityOf(sdlHigh).Should().BeGreaterThan(PriorityOf(sdlLow), "their relative order is kept");
        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "ModSdl", "modB");
    }

    [Fact]
    public async Task AGroupWithoutModsFiles_KeepsItsSlotOnADrag()
    {
        var (loadout, variety, id) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        var menu = await AddMod(loadout, "Menu", 2, "bin/config/r4game/user_config_matrix/pc/x.xml");
        var b = await AddMod(loadout, "B", 3, "Mods/modB/b.ws");

        await variety.MoveItems(id, [Key("modA")], Key("modB"), TargetRelativePosition.BeforeTarget, Db);

        PriorityOf(menu).Should().Be(2, "the menu group keeps its slot");
        PriorityOf(a).Should().BeGreaterThan(PriorityOf(menu));
        PriorityOf(b).Should().BeLessThan(PriorityOf(menu));
        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modA", "modB");
    }

    [Fact]
    public async Task MovingInFileConflicts_ReordersThePage()
    {
        var (loadout, variety, id) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        var b = await AddMod(loadout, "B", 2, "Mods/modB/b.ws");

        await LoadoutManager.ResolveFileConflicts(
            [LoadoutItemGroupPriority.FindByTarget(Db, a).Single().LoadoutItemGroupPriorityId],
            LoadoutItemGroupPriority.FindByTarget(Db, b).Single().LoadoutItemGroupPriorityId);

        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modA", "modB");
    }

    [Fact]
    public async Task AMoveThatBreaksACuratorRule_IsRefusedWithTheRulesText()
    {
        var (loadout, variety, id) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        var b = await AddMod(loadout, "B", 2, "Mods/modB/b.ws");
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutGroupRule.New(tx) { LoadoutId = loadout.LoadoutId, SourceId = b, OtherId = a, Kind = GroupRuleKind.After };
            await tx.Commit();
        }
        await variety.ReconcileSortOrder(id);
        var rule = LoadoutGroupRule.FindByLoadout(Db, loadout.LoadoutId).Single();
        var storedBefore = StoredRows(id);

        var act = async () => await variety.MoveItems(id, [Key("modA")], Key("modB"), TargetRelativePosition.BeforeTarget, Db);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*{GroupRuleText.WinsOver(rule)}*");
        PriorityOf(a).Should().BeLessThan(PriorityOf(b));
        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modB", "modA");
        StoredRows(id).Should().Equal(storedBefore);
        PriorityOf(b).Should().Be(2);
        PriorityOf(a).Should().Be(1);
    }

    [Fact]
    public async Task TheMergedRow_CannotBeMovedOrSwitched()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        await AddMod(loadout, "B", 2, "Mods/modB/b.ws");

        await variety.MoveItems(id, [Key(Merged)], Key("modA"), TargetRelativePosition.AfterTarget, Db);

        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modB", "modA");
        var act = async () => await variety.SetEnabled(id, Key(Merged), false);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DroppingAboveTheMergedRow_PutsTheFolderFirstAmongTheRest()
    {
        var (loadout, variety, id) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        var b = await AddMod(loadout, "B", 2, "Mods/modB/b.ws");
        var c = await AddMod(loadout, "C", 3, "Mods/modC/c.ws");

        await variety.MoveItems(id, [Key("modA")], Key(Merged), TargetRelativePosition.BeforeTarget, Db);

        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modA", "modC", "modB");
        PriorityOf(a).Should().BeGreaterThan(PriorityOf(c));
        PriorityOf(c).Should().BeGreaterThan(PriorityOf(b));
    }

    [Fact]
    public async Task MovingTheFirstRowTowardTheMergedRow_ChangesNothing()
    {
        var (loadout, variety, id) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        var b = await AddMod(loadout, "B", 2, "Mods/modB/b.ws");

        await variety.MoveItemDelta(id, Key("modB"), -1, Db);

        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modB", "modA");
        PriorityOf(a).Should().BeLessThan(PriorityOf(b));
    }

    [Fact]
    public async Task SetEnabled_PersistsTheSwitch()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");

        await variety.SetEnabled(id, Key("modA"), false);

        variety.StoredOrder(Fresh(loadout)).Should().Equal((Merged, true), ("modA", false));
        ((ISortItemDetails)variety.GetSortOrderItems(id, Db)[1]).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task AFolderSwitchedOff_KeepsItsSwitchAcrossAReconcile()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        await variety.SetEnabled(id, Key("modA"), false);

        await variety.ReconcileSortOrder(id);

        variety.StoredOrder(Fresh(loadout)).Should().Equal((Merged, true), ("modA", false));
    }

    [Fact]
    public async Task ASwitchBasedOnARevisionOlderThanTheStoredFolder_IsRefusedInsteadOfStoringASecondRow()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");

        // The load-order manager reconciles in the background after every mod change. If it stores modA's row between
        // the switch reading the folder and committing, a second row for the folder lands beside it, the first row
        // wins the tie and the switch is silently lost.
        var staleDb = Db;
        await variety.ReconcileSortOrder(id);

        var switching = () => variety.SetEnabled(id, Key("modA"), false, staleDb, CancellationToken.None);

        await switching.Should().ThrowAsync<InvalidOperationException>();
        SortOrderItem.FindByParentSortOrder(Db, id).Should().HaveCount(1);
        await variety.SetEnabled(id, Key("modA"), false);
        variety.StoredOrder(Fresh(loadout)).Should().Equal((Merged, true), ("modA", false));
    }

    [Fact]
    public async Task AStaleStoredIndex_DoesNotChangeThePage()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        await AddMod(loadout, "B", 2, "Mods/modB/b.ws");
        await variety.ReconcileSortOrder(id);

        // What a seeding pass may leave behind: the stored indexes in the opposite order.
        var rows = SortOrderItem.FindByParentSortOrder(Db, id).OrderBy(r => r.SortIndex).ToArray();
        using (var tx = Connection.BeginTransaction())
        {
            for (var i = 0; i < rows.Length; i++) tx.Add(rows[i].Id, SortOrderItem.SortIndex, rows.Length - 1 - i);
            await tx.Commit();
        }

        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modB", "modA");
        Order(variety, Fresh(loadout)).Should().Equal(Merged, "modB", "modA");

        await variety.MoveItems(id, [Key("modA")], Key("modB"), TargetRelativePosition.BeforeTarget, Db);
        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modA", "modB");
    }

    [Fact]
    public async Task RankOf_IsTheFoldersHighestPriority()
    {
        var (loadout, variety, _) = await Reconciled();
        await AddMod(loadout, "Low", 4, "Mods/modSDL/a.ws");
        await AddMod(loadout, "High", 9, "Mods/ModSdl/b.ws");
        await AddMod(loadout, "Other", 6, "Mods/modB/b.ws");

        var current = Fresh(loadout);

        variety.RankOf(current, "modsdl").Should().Be(ConflictPriority.From(9));
        variety.RankOf(current, "modB").Should().Be(ConflictPriority.From(6));
        variety.RankOf(current, "modUnknown").Should().BeNull();
    }

    [Fact]
    public async Task RankOf_FollowsAPriorityChange()
    {
        var (loadout, variety, _) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        variety.RankOf(Fresh(loadout), "modA").Should().Be(ConflictPriority.From(1));

        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(LoadoutItemGroupPriority.FindByTarget(Db, a).Single().Id, LoadoutItemGroupPriority.Priority, ConflictPriority.From(7));
            await tx.Commit();
        }

        variety.RankOf(Fresh(loadout), "modA").Should().Be(ConflictPriority.From(7));
    }

    [Fact]
    public async Task ADisabledMod_HasNoRow()
    {
        var (loadout, variety, id) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        await DisableItem(a.Value);

        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged);
    }

    [Fact]
    public async Task SeedingTheStoredIndexes_LeavesThePageOnPriority()
    {
        var (loadout, variety, id) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        var b = await AddMod(loadout, "B", 2, "Mods/modB/b.ws");
        await variety.ReconcileSortOrder(id);

        using (var tx = Connection.BeginTransaction())
        {
            variety.SeedFromGroupOrder(id, [a, b], tx, Db);
            await tx.Commit();
        }

        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modB", "modA");
        PriorityOf(b).Should().BeGreaterThan(PriorityOf(a));
    }

    [Fact]
    public async Task TheChangeSet_FollowsAPriorityChange()
    {
        var (loadout, variety, id) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        await AddMod(loadout, "B", 2, "Mods/modB/b.ws");
        using var cache = variety.GetSortOrderItemsChangeSet(id).AsObservableCache();
        string[] Shown() => cache.Items.OrderBy(i => i.SortIndex).Select(i => i.DisplayName).ToArray();
        Shown().Should().Equal(Merged, "modB", "modA");

        await variety.MoveItems(id, [Key("modA")], Key("modB"), TargetRelativePosition.BeforeTarget, Db);

        for (var i = 0; i < 50 && !Shown().SequenceEqual([Merged, "modA", "modB"]); i++) await Task.Delay(100);
        Shown().Should().Equal(Merged, "modA", "modB");
    }

    [Fact]
    public async Task AMoveReadFromAnOlderRevisionThanACommittedSwitch_StillMakesTheMove()
    {
        var (loadout, variety, id) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        var b = await AddMod(loadout, "B", 2, "Mods/modB/b.ws");
        await AddMod(loadout, "C", 3, "Mods/modC/c.ws");
        await variety.ReconcileSortOrder(id);
        var staleDb = Db;

        // The app (a background reconcile, a switch) commits to the order after the drag read it.
        await variety.SetEnabled(id, Key("modC"), false);

        await variety.MoveItems(id, [Key("modA")], Key("modB"), TargetRelativePosition.BeforeTarget, staleDb);

        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modC", "modA", "modB");
        PriorityOf(a).Should().BeGreaterThan(PriorityOf(b), "A was moved before B");
        variety.StoredOrder(Fresh(loadout)).Should().Contain(("modC", false), "the switch committed meanwhile is kept");
    }

    [Fact]
    public async Task AMoveWhoseDropTargetVanishedMeanwhile_IsDroppedWithoutChangingTheOrder()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        var b = await AddMod(loadout, "B", 2, "Mods/modB/b.ws");
        await variety.ReconcileSortOrder(id);
        var staleDb = Db;

        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(b, recursive: true);
            await tx.Commit();
        }
        await variety.ReconcileSortOrder(id);

        await variety.MoveItems(id, [Key("modA")], Key("modB"), TargetRelativePosition.BeforeTarget, staleDb);

        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modA");
    }

    private async Task<(WitcherModSortOrderVariety Variety, SortOrderId Id, LoadoutItemGroupId Shared, LoadoutItemGroupId Other)> SharedModSetup()
    {
        var (loadout, variety, id) = await Reconciled();
        var shared = await AddMod(loadout, "G", 2, "Mods/modFoo/a.ws", "Mods/modFooUI/b.ws");
        var other = await AddMod(loadout, "H", 1, "Mods/modBar/c.ws");
        await variety.ReconcileSortOrder(id);
        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modFoo", "modFooUI", "modBar");
        return (variety, id, shared, other);
    }

    private async Task ShouldBeRefusedAsOneMod(Func<WitcherModSortOrderVariety, SortOrderId, Task> drag)
    {
        var (variety, id, shared, other) = await SharedModSetup();
        var rowsBefore = StoredRows(id);

        var act = async () => await drag(variety, id);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*modFoo and modFooUI come from the same mod and move together.*");
        PriorityOf(shared).Should().Be(2);
        PriorityOf(other).Should().Be(1);
        StoredRows(id).Should().Equal(rowsBefore);
        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modFoo", "modFooUI", "modBar");
    }

    [Fact]
    public Task AFolderDroppedBetweenTwoFoldersOfOneMod_IsRefused() =>
        ShouldBeRefusedAsOneMod((v, id) => v.MoveItems(id, [Key("modBar")], Key("modFooUI"), TargetRelativePosition.BeforeTarget, Db));

    [Fact]
    public Task AFolderOfASharedModMovedPastAnotherMod_IsRefused() =>
        ShouldBeRefusedAsOneMod((v, id) => v.MoveItems(id, [Key("modFooUI")], Key("modBar"), TargetRelativePosition.AfterTarget, Db));

    [Fact]
    public Task SwappingTwoFoldersOfOneMod_IsRefused() =>
        ShouldBeRefusedAsOneMod((v, id) => v.MoveItems(id, [Key("modFoo")], Key("modFooUI"), TargetRelativePosition.AfterTarget, Db));

    [Fact]
    public async Task MovingAllFoldersOfOneModPastAnother_Works()
    {
        var (variety, id, shared, other) = await SharedModSetup();

        await variety.MoveItems(id, [Key("modFoo"), Key("modFooUI")], Key("modBar"), TargetRelativePosition.AfterTarget, Db);

        PriorityOf(other).Should().BeGreaterThan(PriorityOf(shared));
        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modBar", "modFoo", "modFooUI");
    }

    [Fact]
    public async Task SwitchingAFolder_KeepsItsStoredIndex()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        await AddMod(loadout, "B", 2, "Mods/modB/b.ws");
        await variety.ReconcileSortOrder(id);
        var before = StoredRows(id).Single(r => r.Folder == "modA").SortIndex;

        await variety.SetEnabled(id, Key("modA"), false);

        StoredRows(id).Single(r => r.Folder == "modA").Should().Be(("modA", false, before));
    }

    [Fact]
    public async Task APatchFolderDraggedAheadOfAnotherMod_MovesWhenItsGroupAlsoFillsAHigherFolder()
    {
        var (loadout, variety, id) = await Reconciled();
        var g1 = await AddMod(loadout, "G1", 5, "Mods/modBase/a.ws");
        var g2 = await AddMod(loadout, "G2", 3, "Mods/modOther/o.ws");
        var g3 = await AddMod(loadout, "G3", 1, "Mods/modBase/p.ws", "Mods/modPatch/q.ws");
        await variety.ReconcileSortOrder(id);
        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modBase", "modOther", "modPatch");

        await variety.MoveItems(id, [Key("modPatch")], Key("modOther"), TargetRelativePosition.BeforeTarget, Db);

        PriorityOf(g3).Should().BeGreaterThan(PriorityOf(g2));
        PriorityOf(g1).Should().BeGreaterThan(PriorityOf(g3));
        Folders(variety.GetSortOrderItems(id, Db)).Should().Equal(Merged, "modBase", "modPatch", "modOther");
    }

    [Fact]
    public async Task TheRefusalNamesOnlyTheFoldersOfThatDrag()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "G", 4, "Mods/modFoo/a.ws", "Mods/modFooUI/b.ws");
        await AddMod(loadout, "K", 2, "Mods/modBaz/a.ws", "Mods/modBazUI/b.ws");
        await variety.ReconcileSortOrder(id);

        var act = async () => await variety.MoveItems(id, [Key("modFoo")], Key("modFooUI"), TargetRelativePosition.AfterTarget, Db);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("modFoo and modFooUI come from the same mod and move together.");
    }

    [Fact]
    public async Task ADragBuiltOnAnOutdatedRevision_IsAppliedToTheLatestOrderAndKeepsTheSwitchMadeMeanwhile()
    {
        var (loadout, variety, id) = await Reconciled();
        var a = await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        var b = await AddMod(loadout, "B", 2, "Mods/modB/b.ws");
        await variety.ReconcileSortOrder(id);
        var outdated = Db;
        await variety.SetEnabled(id, Key("modB"), false);

        await variety.MoveItems(id, [Key("modA")], Key("modB"), TargetRelativePosition.BeforeTarget, outdated);

        PriorityOf(a).Should().BeGreaterThan(PriorityOf(b));
        variety.StoredOrder(Fresh(loadout)).Should().Equal((Merged, true), ("modA", true), ("modB", false));
    }
}

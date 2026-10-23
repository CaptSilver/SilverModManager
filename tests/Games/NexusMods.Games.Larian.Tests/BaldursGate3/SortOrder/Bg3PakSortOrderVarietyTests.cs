using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.Larian.BaldursGate3;
using NexusMods.Games.Larian.BaldursGate3.Models;
using NexusMods.Games.Larian.BaldursGate3.SortOrder;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using LoadoutSortOrder = NexusMods.Abstractions.Loadouts.SortOrder;

namespace NexusMods.Games.Larian.Tests.BaldursGate3.SortOrder;

/// <summary>
/// The pak load order of a Baldur's Gate 3 loadout: what it lists, how it keeps the rows of paks that left the
/// loadout, and what a user can move or switch off. Each test drives its own variety instance, the way the game
/// creates one, so no manager reconciles in the background.
/// </summary>
public class Bg3PakSortOrderVarietyTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Bg3PakSortOrderVarietyTests, Larian.BaldursGate3.BaldursGate3>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddBaldursGate3()
        .AddUniversalGameLocator<Larian.BaldursGate3.BaldursGate3>(new Version("1.61"), locationsFileSystem: IsolatedKnownFolders.Create);

    private Bg3PakSortOrderVariety NewVariety() => new(ServiceProvider, Game);

    private IDb Db => Connection.Db;

    private static SortItemKey<string> Key(string name) => new(name.ToLowerInvariant());

    private static GamePath PakPath(string name) => new(Bg3Constants.ModsLocationId, name);

    private record Mod(LoadoutItemGroupId Group, LoadoutFileId[] Files);

    private async Task<Mod> AddMod(Loadout.ReadOnly loadout, string name, params string[] paks)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        var files = paks.Select(p => AddFile(tx, loadout.LoadoutId, group, PakPath(p), p)).ToArray();
        var result = await tx.Commit();
        return new Mod(LoadoutItemGroupId.From(result[group.Value]), files.Select(f => LoadoutFileId.From(result[f.Value])).ToArray());
    }

    private async Task RemoveMod(Mod mod)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var file in mod.Files) tx.Delete(file, recursive: false);
        tx.Delete(mod.Group, recursive: false);
        await tx.Commit();
    }

    /// <summary>
    /// Records the paks as files the game put in the Mods folder, under one group and one item, the way a
    /// reconcile of game-installed content stores them. Returns the group, so a test can switch it off.
    /// </summary>
    private async Task<EntityId> AddGameInstalledPaks(Loadout.ReadOnly loadout, params string[] paks)
    {
        using var tx = Connection.BeginTransaction();
        _ = new GameInstalledGroup.New(tx, out var groupId)
        {
            InstalledForId = loadout,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId)
                {
                    Name = "Installed by the game",
                    LoadoutId = loadout.Id,
                },
            },
        };

        foreach (var pak in paks)
        {
            _ = new GameInstalledItem.New(tx, out var itemId)
            {
                Source = "Bg3InGameMod",
                SourceId = pak,
                Confidence = "GameRecord",
                CanBeClaimed = true,
                DepotShipped = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, itemId)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, itemId)
                    {
                        Name = pak,
                        LoadoutId = loadout.Id,
                        ParentId = groupId,
                    },
                },
            };
            _ = new GameInstalledFile.New(tx, out var fileId)
            {
                Path = PakPath(pak).ToGamePathParentTuple(loadout.Id),
                Hash = Hash.From(0xAB),
                Size = Size.From(10),
                LoadoutItem = new LoadoutItem.New(tx, fileId)
                {
                    Name = pak,
                    LoadoutId = loadout.Id,
                    ParentId = itemId,
                },
            };
        }

        var result = await tx.Commit();
        return result[groupId];
    }

    private async Task<(Loadout.ReadOnly Loadout, Bg3PakSortOrderVariety Variety, SortOrderId SortOrder)> Reconciled()
    {
        var loadout = await CreateLoadout();
        var variety = NewVariety();
        var id = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await variety.ReconcileSortOrder(id);
        return (loadout, variety, id);
    }

    private static string[] Names(IEnumerable<IReactiveSortItem> items) => items.Select(i => i.DisplayName).ToArray();

    private List<(string PakName, int SortIndex, bool IsEnabled)> StoredRows(SortOrderId id) =>
        Connection.Query<(string PakName, int SortIndex, bool IsEnabled)>($"SELECT PakName, SortIndex, IsEnabled FROM bg3.SortOrderItems({Connection}, {id.Value}) ORDER BY SortIndex").ToList();

    [Fact]
    public async Task FreshLoadout_HasAnEmptyOrder()
    {
        var (_, variety, id) = await Reconciled();

        variety.GetSortOrderItems(id, Db).Should().BeEmpty();
        StoredRows(id).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallingAModWithTwoPaks_AppendsTwoRows()
    {
        var (loadout, variety, id) = await Reconciled();

        await AddMod(loadout, "Mod", "First.pak", "Second.pak");
        await variety.ReconcileSortOrder(id);

        Names(variety.GetSortOrderItems(id, Db)).Order().Should().Equal("First.pak", "Second.pak");
        StoredRows(id).Should().HaveCount(2);
        StoredRows(id).Select(r => r.SortIndex).Should().Equal(0, 1);
    }

    [Fact]
    public async Task FilesThatAreNotPaksInTheModsFolder_AreNotListed()
    {
        var (loadout, variety, id) = await Reconciled();

        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mixed");
            _ = AddFile(tx, loadout.LoadoutId, group, PakPath("Real.pak"), "pak");
            _ = AddFile(tx, loadout.LoadoutId, group, PakPath("readme.txt"), "text");
            _ = AddFile(tx, loadout.LoadoutId, group, PakPath("Nested/Deep.pak"), "nested");
            _ = AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, "bin/Other.pak"), "elsewhere");
            await tx.Commit();
        }
        await variety.ReconcileSortOrder(id);

        Names(variety.GetSortOrderItems(id, Db)).Should().Equal("Real.pak");
    }

    [Fact]
    public async Task AGameInstalledPak_IsListedUnlocked_AndFollowsTheGroupSwitch()
    {
        var (loadout, variety, id) = await Reconciled();
        var group = await AddGameInstalledPaks(loadout, "FromGame.pak");
        await variety.ReconcileSortOrder(id);

        var row = variety.GetSortOrderItems(id, Db).Single();
        var details = (ISortItemDetails)row;
        details.IsLocked.Should().BeFalse("every pak drags");
        details.IsEnabled.Should().BeTrue();
        row.IsActive.Should().BeTrue();

        // The row's own switch is ignored for a game-installed name: the group decides.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(StoredRowId(id, "FromGame.pak"), Bg3PakSortOrderItem.IsEnabled, false);
            await tx.Commit();
        }
        row = variety.GetSortOrderItems(id, Db).Single();
        row.IsActive.Should().BeTrue("the group is on, whatever the stored row says");
        ((ISortItemDetails)row).IsEnabled.Should().BeTrue();

        await DisableItem(group);

        row = variety.GetSortOrderItems(id, Db).Single();
        row.IsActive.Should().BeFalse();
        ((ISortItemDetails)row).IsEnabled.Should().BeFalse();
        ((ISortItemDetails)row).IsLocked.Should().BeFalse();
    }

    private EntityId StoredRowId(SortOrderId id, string name) =>
        Connection.Query<EntityId>($"SELECT Id FROM bg3.SortOrderItems({Connection}, {id.Value}) WHERE PakName = {name}").Single();

    [Fact]
    public async Task TheLoadoutCopyOfAName_WinsOverTheGameInstalledRow()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddGameInstalledPaks(loadout, "Shared.pak");
        var mod = await AddMod(loadout, "Mod", "shared.pak");
        await variety.ReconcileSortOrder(id);

        var row = variety.GetSortOrderItems(id, Db).Single();
        row.ModGroupId.Value.Value.Should().Be(mod.Group.Value);
        row.ModName.Should().Be("Mod");
        ((Bg3PakLoadoutData)row.LoadoutData!).IsGameInstalled.Should().BeFalse();
    }

    [Fact]
    public async Task RemovingTheMod_HidesItsRowsAsTombstones_AndReAddingRestoresPositionAndSwitch()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "A", "A.pak");
        var middle = await AddMod(loadout, "B", "B.pak");
        await AddMod(loadout, "C", "C.pak");
        await variety.ReconcileSortOrder(id);
        await variety.MoveItems(id, [Key("B.pak")], Key("A.pak"), TargetRelativePosition.BeforeTarget, Db);
        await variety.SetEnabled(id, Key("B.pak"), false);
        var original = Names(variety.GetSortOrderItems(id, Db));

        await RemoveMod(middle);
        await variety.ReconcileSortOrder(id);

        Names(variety.GetSortOrderItems(id, Db)).Should().NotContain("B.pak");
        StoredRows(id).Select(r => r.PakName).Should().Contain("B.pak", "the row stays as a tombstone");

        await AddMod(loadout, "B again", "B.pak");
        await variety.ReconcileSortOrder(id);

        var items = variety.GetSortOrderItems(id, Db);
        Names(items).Should().Equal(original);
        ((ISortItemDetails)items.Single(i => i.DisplayName == "B.pak")).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task MoveItems_PersistsForALoadoutPak_AndAGameInstalledPak()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mod", "A.pak", "B.pak");
        await AddGameInstalledPaks(loadout, "G.pak");
        await variety.ReconcileSortOrder(id);
        var initial = Names(variety.GetSortOrderItems(id, Db));
        initial.Should().HaveCount(3);

        await variety.MoveItems(id, [Key("G.pak")], Key(initial[0]), TargetRelativePosition.BeforeTarget, Db);
        await variety.MoveItems(id, [Key("B.pak")], Key("G.pak"), TargetRelativePosition.BeforeTarget, Db);

        var moved = Names(variety.GetSortOrderItems(id, Db));
        moved[0].Should().Be("B.pak");
        moved[1].Should().Be("G.pak");
        StoredRows(id).Select(r => r.PakName).Should().Equal(moved);
    }

    [Fact]
    public async Task SetEnabled_PersistsForALoadoutPak_AndThrowsForAGameInstalledOne()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mod", "A.pak");
        await AddGameInstalledPaks(loadout, "G.pak");
        await variety.ReconcileSortOrder(id);

        await ((IToggleableSortOrderVariety)variety).SetEnabled(id, Key("A.pak"), false);

        StoredRows(id).Single(r => r.PakName == "A.pak").IsEnabled.Should().BeFalse();
        var a = variety.GetSortOrderItems(id, Db).Single(i => i.DisplayName == "A.pak");
        ((ISortItemDetails)a).IsEnabled.Should().BeFalse();
        a.IsActive.Should().BeFalse();

        var toggle = () => variety.SetEnabled(id, Key("G.pak"), false);
        await toggle.Should().ThrowAsync<InvalidOperationException>();
        StoredRows(id).Single(r => r.PakName == "G.pak").IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task TwoLoadouts_DoNotSeeEachOthersRows()
    {
        var (first, firstVariety, firstId) = await Reconciled();
        var (second, secondVariety, secondId) = await Reconciled();

        await AddMod(first, "Mod", "Only-first.pak");
        await AddMod(second, "Mod", "Only-second.pak");
        await AddGameInstalledPaks(second, "Game-second.pak");
        await firstVariety.ReconcileSortOrder(firstId);
        await secondVariety.ReconcileSortOrder(secondId);

        Names(firstVariety.GetSortOrderItems(firstId, Db)).Should().Equal("Only-first.pak");
        Names(secondVariety.GetSortOrderItems(secondId, Db)).Order().Should().Equal("Game-second.pak", "Only-second.pak");
    }

    [Fact]
    public async Task ANoticeWrittenWhileAMoveIsInFlight_DoesNotTripTheSortOrderChangedGuard()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mod", "A.pak", "B.pak");
        await variety.ReconcileSortOrder(id);
        var before = Db;
        var names = Names(variety.GetSortOrderItems(id, Db));
        var txBefore = SortOrderTx(id, before);

        using (var tx = Connection.BeginTransaction())
        {
            _ = new Bg3ModListNotice.New(tx)
            {
                SortOrderId = id,
                Summary = "modsettings.lsx was edited outside the app.",
                ReplacedAt = DateTimeOffset.UtcNow,
            };
            await tx.Commit();
        }
        SortOrderTx(id, Db).Should().Be(txBefore, "the notice is not part of the sort order");

        await variety.MoveItems(id, [Key(names[1])], Key(names[0]), TargetRelativePosition.BeforeTarget, before);

        Names(variety.GetSortOrderItems(id, Db)).Should().Equal(names[1], names[0]);
    }

    [Fact]
    public async Task ReplacedEditSummary_FollowsTheNoticeAndDismissDeletesIt()
    {
        var (_, variety, id) = await Reconciled();
        var seen = new List<string?>();
        using var subscription = ((IReplacedGameEditSource)variety).ReplacedEditSummary(id).Subscribe(seen.Add);

        using (var tx = Connection.BeginTransaction())
        {
            _ = new Bg3ModListNotice.New(tx)
            {
                SortOrderId = id,
                Summary = "modsettings.lsx was edited outside the app.",
                ReplacedAt = DateTimeOffset.UtcNow,
            };
            await tx.Commit();
        }
        await WaitUntil(() => seen.Contains("modsettings.lsx was edited outside the app."));

        await ((IReplacedGameEditSource)variety).Dismiss(id);
        await WaitUntil(() => seen[^1] is null);

        seen[0].Should().BeNull("nothing is waiting before the notice");
        Connection.Db.Datoms(Bg3ModListNotice.SortOrder, id).Should().BeEmpty();
    }

    [Fact]
    public async Task StageOrder_AndStageEnabled_WriteIntoTheCallersTransaction()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mod", "A.pak", "B.pak");
        await variety.ReconcileSortOrder(id);

        using (var tx = Connection.BeginTransaction())
        {
            variety.StageOrder(id, ["B.pak", "A.pak"], tx);
            variety.StageEnabled(id, "B.pak", false, tx);
            await tx.Commit();
        }

        var stored = variety.StoredOrder(Loadout.Load(Db, loadout.LoadoutId));
        stored.Select(r => r.Name).Should().Equal("B.pak", "A.pak");
        stored.Single(r => r.Name == "B.pak").IsEnabled.Should().BeFalse();

        using (var tx = Connection.BeginTransaction())
        {
            variety.StageOrder(id, [("Later.pak", (bool?)false)], tx);
            await tx.Commit();
        }
        StoredRows(id).Single(r => r.PakName == "Later.pak").IsEnabled.Should().BeFalse("a staged name the loadout does not have yet is kept hidden");
        Names(variety.GetSortOrderItems(id, Db)).Should().NotContain("Later.pak");
    }

    [Fact]
    public async Task ClearReplacedGameEdit_DeletesTheNotice()
    {
        var (_, variety, id) = await Reconciled();
        using (var tx = Connection.BeginTransaction())
        {
            _ = new Bg3ModListNotice.New(tx)
            {
                SortOrderId = id,
                Summary = "edited",
                ReplacedAt = DateTimeOffset.UtcNow,
            };
            await tx.Commit();
        }

        await variety.ClearReplacedGameEdit(id);

        Connection.Db.Datoms(Bg3ModListNotice.SortOrder, id).Should().BeEmpty();
    }

    private static TxId SortOrderTx(SortOrderId id, IDb db) =>
        new[] { db.Get(id).Max(d => d.T) }.Concat(db.Datoms(SortOrderItem.ParentSortOrder, id).Select(d => db.Get(d.E).Max(x => x.T))).Max();

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
            await Task.Delay(25, cts.Token);
    }
}

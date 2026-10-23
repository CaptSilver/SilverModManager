using System.Text;
using DynamicData;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.PluginLists;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.Models;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Games.CreationEngine.Tests.SkyrimSE;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using LoadoutSortOrder = SilverModManager.Abstractions.Loadouts.SortOrder;

namespace SilverModManager.Games.CreationEngine.Tests.SortOrder;

/// <summary>
/// The plugin load order of a Skyrim SE loadout: what it lists, how it keeps the rows of plugins that left the
/// loadout, and what a user can move or switch off. Each test drives its own variety instance, the way the game
/// creates one, so no manager reconciles in the background.
/// </summary>
public class PluginSortOrderVarietyTests(ITestOutputHelper helper)
    : AIsolatedGameTest<PluginSortOrderVarietyTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    private const string DataFolder = "Data";
    private static readonly string[] VanillaMasters = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create);

    private PluginSortOrderVariety NewVariety() => new(ServiceProvider, Game);

    private IDb Db => Connection.Db;

    private static SortItemKey<string> Key(string name) => new(name.ToLowerInvariant());

    /// <summary>
    /// Records the vanilla masters as game files of the loadout's installation, with readable plugin bytes so the
    /// header cache can tell they are masters.
    /// </summary>
    private async Task SeedGameFiles(Loadout.ReadOnly loadout)
    {
        var local = ((StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>()).Local.Connection;
        var install = loadout.Installation;

        using var tx = local.BeginTransaction();
        var pathIds = new List<EntityId>();
        foreach (var file in VanillaMasters)
        {
            var bytes = SkyrimPluginFile.Bytes(file, file == "Skyrim.esm" ? [] : ["Skyrim.esm"]);
            var hashes = await MultiHasher.HashStream(new MemoryStream(bytes));
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(file, new MemoryStream(bytes)), hashes.XxHash3, Size.FromLong(bytes.Length))]);

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
            pathIds.Add(new PathHashRelation.New(tx)
            {
                Path = RelativePath.FromUnsanitizedInput($"{DataFolder}/{file}"),
                HashId = relation,
            }.Id);
        }

        _ = new LocalBaseline.New(tx)
        {
            Store = install.Store,
            GamePath = install.Path,
            LocatorIds = [],
            Kind = LocalBaselineKind.Snapshot,
            VersionName = "",
            BuiltAt = DateTimeOffset.UnixEpoch,
            FilesIds = pathIds.ToArray(),
        };
        await tx.Commit();
    }

    private record Mod(LoadoutItemGroupId Group, LoadoutFileId[] Files);

    private async Task<Mod> AddMod(Loadout.ReadOnly loadout, string name, params string[] plugins)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        var files = plugins.Select(p => AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"{DataFolder}/{p}"))).ToArray();
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

    private async Task<(Loadout.ReadOnly Loadout, PluginSortOrderVariety Variety, SortOrderId SortOrder)> Reconciled()
    {
        // Created before the plugins are stored: creating a loadout runs the garbage collector.
        var loadout = await CreateLoadout();
        await SeedGameFiles(loadout);
        var variety = NewVariety();
        var id = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await variety.ReconcileSortOrder(id);
        return (loadout, variety, id);
    }

    private static string[] Names(IEnumerable<IReactiveSortItem> items) => items.Select(i => i.DisplayName).ToArray();

    private List<(string PluginName, int SortIndex, bool IsEnabled)> StoredRows(SortOrderId id) =>
        Connection.Query<(string PluginName, int SortIndex, bool IsEnabled)>($"SELECT PluginName, SortIndex, IsEnabled FROM plugins.SortOrderItems({Connection}, {id.Value}) ORDER BY SortIndex").ToList();

    [Fact]
    public async Task FreshLoadout_ListsTheGameMastersFirstAndEnabled()
    {
        var (_, variety, id) = await Reconciled();

        var items = variety.GetSortOrderItems(id, Db);

        Names(items).Should().Equal(VanillaMasters);
        items.Should().OnlyContain(i => ((ISortItemDetails)i).IsEnabled && i.IsActive);
        ((ISortItemDetails)items[1]).Masters.Should().Equal("Skyrim.esm");
        ((ISortItemDetails)items[0]).Masters.Should().BeEmpty();
    }

    [Fact]
    public async Task ModWithTwoPlugins_AppendsTwoItemsAfterTheMasters()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Two Plugins", "Beta.esp", "Alpha.esp");

        await variety.ReconcileSortOrder(id);

        Names(variety.GetSortOrderItems(id, Db)).Should().Equal([.. VanillaMasters, "Alpha.esp", "Beta.esp"]);
        StoredRows(id).Select(r => r.SortIndex).Should().Equal(Enumerable.Range(0, VanillaMasters.Length + 2));
    }

    [Fact]
    public async Task RemovingAMod_HidesItsRowsWithoutDeletingThem()
    {
        var (loadout, variety, id) = await Reconciled();
        var mod = await AddMod(loadout, "Gone Soon", "Alpha.esp", "Beta.esp");
        await variety.ReconcileSortOrder(id);

        await RemoveMod(mod);
        await variety.ReconcileSortOrder(id);

        Names(variety.GetSortOrderItems(id, Db)).Should().Equal(VanillaMasters);
        StoredRows(id).Select(r => r.PluginName).Should().Contain(["Alpha.esp", "Beta.esp"], "a tombstone is kept");
    }

    [Fact]
    public async Task ReAddingAMod_RestoresItsRowsAtTheirOldPositionsAndKeepsASwitchedOffPluginOff()
    {
        var (loadout, variety, id) = await Reconciled();
        // Each mod is reconciled on its own, so the order follows the install order rather than the names.
        await AddMod(loadout, "Before", "Last.esp");
        await variety.ReconcileSortOrder(id);
        await AddMod(loadout, "Also before", "First.esp");
        await variety.ReconcileSortOrder(id);
        var cycled = await AddMod(loadout, "Cycled", "Alpha.esp", "Beta.esp");
        await variety.ReconcileSortOrder(id);
        await variety.SetEnabled(id, Key("Alpha.esp"), false);
        var original = Names(variety.GetSortOrderItems(id, Db));
        original.Should().Equal([.. VanillaMasters, "Last.esp", "First.esp", "Alpha.esp", "Beta.esp"]);

        await RemoveMod(cycled);
        await variety.ReconcileSortOrder(id);
        Names(variety.GetSortOrderItems(id, Db)).Should().Equal([.. VanillaMasters, "Last.esp", "First.esp"]);

        await AddMod(loadout, "Cycled again", "Alpha.esp", "Beta.esp");
        await variety.ReconcileSortOrder(id);

        var items = variety.GetSortOrderItems(id, Db);
        Names(items).Should().Equal(original);
        ((ISortItemDetails)items.Single(i => i.DisplayName == "Alpha.esp")).IsEnabled.Should().BeFalse();
        ((ISortItemDetails)items.Single(i => i.DisplayName == "Beta.esp")).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task MoveItemDelta_OverATombstone_SwapsTheTwoVisibleRows()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "A", "A.esp");
        var middle = await AddMod(loadout, "B", "B.esp");
        await AddMod(loadout, "C", "C.esp");
        await variety.ReconcileSortOrder(id);
        await RemoveMod(middle);
        await variety.ReconcileSortOrder(id);

        await variety.MoveItemDelta(id, Key("A.esp"), 1, Db);

        Names(variety.GetSortOrderItems(id, Db)).Should().Equal([.. VanillaMasters, "C.esp", "A.esp"]);
        StoredRows(id).Select(r => r.PluginName).Should().Contain("B.esp");
    }

    [Fact]
    public async Task MoveItems_ReordersAndPersists()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mods", "A.esp", "B.esp", "C.esp");
        await variety.ReconcileSortOrder(id);

        await variety.MoveItems(id, [Key("C.esp")], Key("A.esp"), TargetRelativePosition.BeforeTarget, Db);

        Names(variety.GetSortOrderItems(id, Db)).Should().Equal([.. VanillaMasters, "C.esp", "A.esp", "B.esp"]);
        StoredRows(id).Select(r => r.PluginName).Should().Equal([.. VanillaMasters, "C.esp", "A.esp", "B.esp"]);
    }

    [Fact]
    public async Task SetEnabled_Persists_AndIsActiveFollowsTheModsEnabledState()
    {
        var (loadout, variety, id) = await Reconciled();
        var mod = await AddMod(loadout, "Mod", "A.esp", "B.esp");
        await variety.ReconcileSortOrder(id);

        await variety.SetEnabled(id, Key("A.esp"), false);
        await DisableItem(mod.Group.Value);

        var items = variety.GetSortOrderItems(id, Db);
        var a = items.Single(i => i.DisplayName == "A.esp");
        var b = items.Single(i => i.DisplayName == "B.esp");
        ((ISortItemDetails)a).IsEnabled.Should().BeFalse();
        a.IsActive.Should().BeFalse();
        ((ISortItemDetails)b).IsEnabled.Should().BeTrue("the plugin's own switch is separate from its mod's");
        b.IsActive.Should().BeFalse("its mod is disabled");
        StoredRows(id).Single(r => r.PluginName == "A.esp").IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task SetEnabled_ThroughTheToggleableInterface_AcceptsTheItemKey()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mod", "A.esp");
        await variety.ReconcileSortOrder(id);

        await ((IToggleableSortOrderVariety)variety).SetEnabled(id, Key("A.esp"), false);

        variety.GetSortOrderItems(id, Db).Single(i => i.DisplayName == "A.esp").Should().BeAssignableTo<ISortItemDetails>().Which.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ToggleThroughTheInterface_Persists_AndThePluginListNoLongerStarsIt()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mod", "A.esp", "B.esp");
        await variety.ReconcileSortOrder(id);

        await ((IToggleableSortOrderVariety)variety).SetEnabled(id, Key("B.esp"), false);

        // A fresh variety reads the switch back from the database rather than from anything cached.
        var stored = NewVariety().StoredOrder(Loadout.Load(Db, loadout.LoadoutId));
        stored.Single(row => row.Name == "B.esp").IsEnabled.Should().BeFalse();
        stored.Single(row => row.Name == "A.esp").IsEnabled.Should().BeTrue();

        var game = Substitute.For<ICreationEngineGame>();
        game.DataPath.Returns(new GamePath(LocationId.Game, DataFolder));
        game.PluginsFile.Returns(new GamePath(LocationId.AppData, "Plugins.txt"));
        game.ParsePlugin(Hash.From(1), null).ReturnsForAnyArgs(call => ValueTask.FromResult<PluginHeader?>(
            new PluginHeader(call.ArgAt<RelativePath?>(1)!.Value.FileName.ToString(), [], 0, null, null, null, null, PluginFormat.Tes4)));
        game.IsMaster(default!, default!).ReturnsForAnyArgs(false);
        game.IsBlueprint(default!).ReturnsForAnyArgs(false);
        var tree = new[] { "A.esp", "B.esp" }.ToDictionary(
            name => new GamePath(LocationId.Game, $"{DataFolder}/{name}"),
            _ => new SyncNode
            {
                Loadout = new SyncNodePart { Hash = Hash.From(1), Size = Size.From(10), LastModifiedTicks = 0 },
                SourceItemType = LoadoutSourceItemType.Game,
            });

        using var stream = new MemoryStream();
        await new PluginsFile(NullLogger<PluginsFile>.Instance, game, new AsteriskMechanism())
            .Render(stream, null!, [], stored.Where(row => row.Name is "A.esp" or "B.esp").ToArray(), tree);

        Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .Should().Equal("*A.esp", "B.esp");
    }

    [Fact]
    public async Task LockedRows_RefuseMovesAndToggles()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mod", "A.esp");
        await variety.ReconcileSortOrder(id);
        var expected = Names(variety.GetSortOrderItems(id, Db));

        await variety.MoveItemDelta(id, Key("Update.esm"), 1, Db);
        Names(variety.GetSortOrderItems(id, Db)).Should().Equal(expected, "a locked row does not move");

        await variety.MoveItems(id, [Key("A.esp")], Key("Skyrim.esm"), TargetRelativePosition.BeforeTarget, Db);
        Names(variety.GetSortOrderItems(id, Db)).Should().Equal(expected, "nothing moves in front of the early loaders");

        var items = variety.GetSortOrderItems(id, Db);
        ((ISortItemDetails)items[0]).IsLocked.Should().BeTrue();
        ((ISortItemDetails)items.Single(i => i.DisplayName == "A.esp")).IsLocked.Should().BeFalse();

        var toggle = () => variety.SetEnabled(id, Key("Skyrim.esm"), false);
        await toggle.Should().ThrowAsync<InvalidOperationException>();
        ((ISortItemDetails)variety.GetSortOrderItems(id, Db)[0]).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task AGameInstalledRow_IsMovableButToggleLockedAndNamesItsGroup()
    {
        var (loadout, variety, id) = await Reconciled();
        await Connection.AddGameInstalledPlugin(loadout, "Creation Title", "Data/ccnew.esl");
        await Connection.AddGameInstalledPlugin(loadout, "Other Title", "Data/ccother.esl");
        await variety.ReconcileSortOrder(id);

        var row = (ISortItemDetails)variety.GetSortOrderItems(id, Db).Single(i => i.DisplayName == "ccnew.esl");
        row.IsLocked.Should().BeFalse();
        row.LockReason.Should().Be(SortItemLockReason.GameInstalledGroup);

        var toggle = () => variety.SetEnabled(id, Key("ccnew.esl"), false);
        (await toggle.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Installed by the game");

        await variety.MoveItems(id, [Key("ccother.esl")], Key("ccnew.esl"), TargetRelativePosition.BeforeTarget, Db);
        Names(variety.GetSortOrderItems(id, Db)).Should().Equal([.. VanillaMasters, "ccother.esl", "ccnew.esl"]);
        StoredRows(id).Select(r => r.PluginName).Should().Equal([.. VanillaMasters, "ccother.esl", "ccnew.esl"]);
    }

    [Fact]
    public async Task AGameInstalledRowWithAPersistedOffSwitch_IsActiveExactlyWhileTheGroupIsOn()
    {
        var (loadout, variety, id) = await Reconciled();
        var (group, _) = await Connection.AddGameInstalledPlugin(loadout, "Creation Title", "Data/ccnew.esl");
        await variety.ReconcileSortOrder(id);

        // The row's own switch is ignored for a game-installed name, so an off value left in it changes nothing.
        using (var tx = Connection.BeginTransaction())
        {
            var rowId = Connection.Query<EntityId>($"SELECT Id FROM plugins.SortOrderItems({Connection}, {id.Value}) WHERE lower(PluginName) = 'ccnew.esl'").Single();
            tx.Add(rowId, PluginSortOrderItem.IsEnabled, false);
            await tx.Commit();
        }

        variety.GetSortOrderItems(id, Db).Single(i => i.DisplayName == "ccnew.esl").IsActive.Should().BeTrue("the group is on");

        await DisableItem(group);
        var off = variety.GetSortOrderItems(id, Db).Single(i => i.DisplayName == "ccnew.esl");
        off.IsActive.Should().BeFalse("the group is off");
        ((ISortItemDetails)off).IsEnabled.Should().BeFalse("the row mirrors the group");

        await EnableItem(group);
        var on = variety.GetSortOrderItems(id, Db).Single(i => i.DisplayName == "ccnew.esl");
        on.IsActive.Should().BeTrue();
        ((ISortItemDetails)on).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task AnEarlyLoaderInstalledByTheGame_IsLockedAsAlwaysLoaded()
    {
        var (loadout, variety, id) = await Reconciled();
        await Connection.AddGameInstalledPlugin(loadout, "Base Game", "Data/Skyrim.esm");
        await variety.ReconcileSortOrder(id);

        var row = (ISortItemDetails)variety.GetSortOrderItems(id, Db).Single(i => i.DisplayName == "Skyrim.esm");
        row.IsLocked.Should().BeTrue();
        row.LockReason.Should().Be(SortItemLockReason.AlwaysLoaded);
    }

    [Fact]
    public async Task AModsRow_HasNoLockReason()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mod", "A.esp");
        await variety.ReconcileSortOrder(id);

        ((ISortItemDetails)variety.GetSortOrderItems(id, Db).Single(i => i.DisplayName == "A.esp")).LockReason.Should().BeNull();
    }

    [Fact]
    public async Task APluginWhoseHeaderCannotBeRead_StillGetsARow()
    {
        var loadout = await CreateLoadout();
        await SeedGameFiles(loadout);
        var garbage = Encoding.UTF8.GetBytes("not a plugin");
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("Broken.esp", new MemoryStream(garbage)), garbage.xxHash3(), Size.FromLong(garbage.Length))]);
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "Broken");
            _ = AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"{DataFolder}/Broken.esp"), "not a plugin");
            _ = AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"{DataFolder}/Missing.esp"), "bytes no store holds");
            await tx.Commit();
        }
        var variety = NewVariety();
        var id = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);

        await variety.ReconcileSortOrder(id);

        var items = variety.GetSortOrderItems(id, Db);
        Names(items).Should().Equal([.. VanillaMasters, "Broken.esp", "Missing.esp"]);
        ((ISortItemDetails)items[^1]).Masters.Should().BeEmpty();
    }

    [Fact]
    public async Task ANoticeWrittenWhileAMoveIsInFlight_DoesNotTripTheSortOrderChangedGuard()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mods", "A.esp", "B.esp");
        await variety.ReconcileSortOrder(id);
        var before = Db;
        var txBefore = SortOrderTx(id, before);

        using (var tx = Connection.BeginTransaction())
        {
            _ = new PluginListNotice.New(tx)
            {
                SortOrderId = id,
                Summary = "Plugins.txt was edited outside the app.",
                ReplacedAt = DateTimeOffset.UtcNow,
            };
            await tx.Commit();
        }
        SortOrderTx(id, Db).Should().Be(txBefore, "the notice is not part of the sort order");

        await variety.MoveItems(id, [Key("B.esp")], Key("A.esp"), TargetRelativePosition.BeforeTarget, before);

        Names(variety.GetSortOrderItems(id, Db)).Should().Equal([.. VanillaMasters, "B.esp", "A.esp"]);
    }

    private static TxId SortOrderTx(SortOrderId id, IDb db) =>
        new[] { db.Get(id).Max(d => d.T) }.Concat(db.Datoms(SortOrderItem.ParentSortOrder, id).Select(d => db.Get(d.E).Max(x => x.T))).Max();

    [Fact]
    public async Task ReplacedEditSummary_FollowsTheNoticeAndDismissDeletesIt()
    {
        var (_, variety, id) = await Reconciled();
        var seen = new List<string?>();
        using var subscription = ((IReplacedGameEditSource)variety).ReplacedEditSummary(id).Subscribe(seen.Add);

        using (var tx = Connection.BeginTransaction())
        {
            _ = new PluginListNotice.New(tx)
            {
                SortOrderId = id,
                Summary = "Plugins.txt was edited outside the app.",
                ReplacedAt = DateTimeOffset.UtcNow,
            };
            await tx.Commit();
        }
        await WaitUntil(() => seen.Contains("Plugins.txt was edited outside the app."));

        await ((IReplacedGameEditSource)variety).Dismiss(id);
        await WaitUntil(() => seen[^1] is null);

        seen[0].Should().BeNull("nothing is waiting before the notice");
        Connection.Db.Datoms(PluginListNotice.SortOrder, id).Should().BeEmpty();
    }

    [Fact]
    public async Task StageOrder_AndStageEnabled_WriteIntoTheCallersTransaction()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mods", "A.esp", "B.esp");
        await variety.ReconcileSortOrder(id);

        using (var tx = Connection.BeginTransaction())
        {
            variety.StageOrder(id, ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "B.esp", "A.esp"], tx);
            variety.StageEnabled(id, "B.esp", false, tx);
            await tx.Commit();
        }

        StoredRows(id).Select(r => r.PluginName).Should().Equal([.. VanillaMasters, "B.esp", "A.esp"]);
        StoredRows(id).Single(r => r.PluginName == "B.esp").IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task StageOrder_WithSwitches_KeepsAPluginTheLoadoutLacksUntilItArrives()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mods", "A.esp");
        await variety.ReconcileSortOrder(id);

        using (var tx = Connection.BeginTransaction())
        {
            variety.StageOrder(id, [.. VanillaMasters.Select(name => (name, (bool?)null)), ("Later.esp", false), ("A.esp", null)], tx);
            await tx.Commit();
        }

        variety.GetSortOrderItems(id, Connection.Db).Select(item => item.DisplayName).Should().Equal([.. VanillaMasters, "A.esp"],
            "a row without a plugin stays out of the page");

        await AddMod(loadout, "Late", "Later.esp");
        await variety.ReconcileSortOrder(id);

        var items = variety.GetSortOrderItems(id, Connection.Db);
        items.Select(item => item.DisplayName).Should().Equal([.. VanillaMasters, "Later.esp", "A.esp"]);
        items.Single(item => item.DisplayName == "Later.esp").IsEnabled.Should().BeFalse();
        items.Single(item => item.DisplayName == "A.esp").IsEnabled.Should().BeTrue("a null switch leaves the row as it was");
    }

    [Fact]
    public async Task DeleteSortOrder_RemovesTombstonesToo()
    {
        var (loadout, variety, id) = await Reconciled();
        var mod = await AddMod(loadout, "Gone", "A.esp");
        await variety.ReconcileSortOrder(id);
        await RemoveMod(mod);
        await variety.ReconcileSortOrder(id);

        await variety.DeleteSortOrder(id);

        Connection.Db.Datoms(SortOrderItem.ParentSortOrder, id).Should().BeEmpty();
        variety.GetSortOrderIdFor(loadout.LoadoutId, Db).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task ChangeSet_ListsTheLiveRows()
    {
        var (loadout, variety, id) = await Reconciled();
        await AddMod(loadout, "Mod", "A.esp");
        await variety.ReconcileSortOrder(id);
        var names = new List<string>();

        Exception? error = null;
        using var subscription = variety.GetSortOrderItemsChangeSet(id)
            .Subscribe(changes => names.AddRange(changes.Where(c => c.Reason == ChangeReason.Add).Select(c => c.Current.DisplayName)), e => error = e);
        await WaitUntil(() => error is not null || names.Count >= VanillaMasters.Length + 1);

        error.Should().BeNull();

        names.Should().BeEquivalentTo([.. VanillaMasters, "A.esp"]);
    }

    [Fact]
    public void TheGamesOwnManager_ListsThePluginVariety()
    {
        var varieties = Game.SortOrderManager.GetSortOrderVarieties();

        varieties.Should().ContainSingle().Which.Should().BeOfType<PluginSortOrderVariety>()
            .Which.SortOrderVarietyId.Value.Should().Be(new Guid("6F2B1C0E-7D3A-4B8E-9A61-3C5D2E8F4A17"));
    }

    [Fact]
    public async Task SiblingGameLoadouts_AreNotSortedByThisGamesManager()
    {
        var variety = (PluginSortOrderVariety)Game.SortOrderManager.GetSortOrderVarieties().Single();

        // Creating a loadout walks every loadout's installation, so the seeded sibling comes after it. The sibling's
        // mod still lands before the own mod, so whatever the manager does with it has happened by the time it has
        // dealt with the mod that is its own.
        var own = await CreateLoadout();
        await SeedGameFiles(own);
        var sibling = await SeedLoadout(CreationEngine.SkyrimVR.SkyrimVR.GameId);
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, sibling, "Sibling mod");
            AddFile(tx, sibling, group, new GamePath(LocationId.Game, $"{DataFolder}/Sibling.esp"));
            await tx.Commit();
        }
        await AddMod(own, "Own mod", "Own.esp");

        await WaitUntil(() => variety.GetSortOrderIdFor(own.LoadoutId, Db).HasValue
            && variety.GetSortOrderItems(variety.GetSortOrderIdFor(own.LoadoutId, Db).Value, Db).Any(i => i.DisplayName == "Own.esp"));

        variety.GetSortOrderIdFor(sibling, Db).HasValue.Should().BeFalse("the sibling is another game even though both share a Nexus id");
        var ownItems = variety.GetSortOrderItems(variety.GetSortOrderIdFor(own.LoadoutId, Db).Value, Db);
        Names(ownItems).Should().NotContain("Sibling.esp");
    }

    private async Task<LoadoutId> SeedLoadout(GameId game)
    {
        using var tx = Connection.BeginTransaction();
        var install = new GameInstallMetadata.New(tx)
        {
            Store = GameStore.Unknown,
            Path = "/games/" + Guid.NewGuid(),
            GameId = CreationEngine.SkyrimSE.SkyrimSE.NexusModsGameId.Value,
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

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
            await Task.Delay(25, cts.Token);
    }
}

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.Models;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using LoadoutSortOrder = SilverModManager.Abstractions.Loadouts.SortOrder;

namespace SilverModManager.Games.CreationEngine.Tests.SortOrder;

/// <summary>
/// The <c>plugins</c> query macros over a Skyrim SE loadout. Vanilla plugins are not loadout items, they only
/// show up as game-file rows of the hash stores, so the game files are seeded the way a local baseline
/// would hold them.
/// </summary>
public class PluginQueriesTests(ITestOutputHelper helper)
    : AIsolatedGameTest<PluginQueriesTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    private const string DataFolder = "Data";
    private static readonly string[] VanillaMasters = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create);

    private record PluginRow(string Name, Hash Hash, bool IsEnabled, string ModName, EntityId? ModGroupId, bool IsGameInstalled);

    private IDb Db => Connection.Db;

    private List<PluginRow> LoadoutPlugins(Loadout.ReadOnly loadout) =>
        Connection.Query<(string Name, Hash Hash, bool IsEnabled, string ModName, EntityId? ModGroupId, bool IsGameInstalled)>(
                $"SELECT * FROM plugins.LoadoutPlugins({Connection}, {loadout.LoadoutId}, {LocationId.Game.Value}, {DataFolder})")
            .Select(r => new PluginRow(r.Name, r.Hash, r.IsEnabled, r.ModName, r.ModGroupId, r.IsGameInstalled))
            .ToList();

    /// <summary>Records <paramref name="files"/> as vanilla game files of the loadout's installation.</summary>
    private async Task SeedGameFiles(Loadout.ReadOnly loadout, params string[] files)
    {
        var local = ((StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>()).Local.Connection;
        var install = loadout.Installation;

        using var tx = local.BeginTransaction();
        var pathIds = new List<EntityId>();
        foreach (var file in files)
        {
            var hashes = await MultiHasher.HashStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("vanilla " + file)));
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
                Path = RelativePath.FromUnsanitizedInput(file),
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

    private async Task<EntityId> AddMod(Loadout.ReadOnly loadout, string name, params string[] paths)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        foreach (var path in paths)
            AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, path));
        var result = await tx.Commit();
        return result[group.Value];
    }

    private async Task<Loadout.ReadOnly> VanillaLoadout()
    {
        var loadout = await CreateLoadout();
        await SeedGameFiles(loadout, [.. VanillaMasters.Select(m => $"{DataFolder}/{m}"), "Data/Textures/skip.dds", "Skyrim.exe", "Data/Sub/Nested.esp"]);
        return loadout;
    }

    [Fact]
    public async Task LoadoutPlugins_FreshLoadout_ListsTheGameMastersFromTheGameFileRows()
    {
        var loadout = await VanillaLoadout();

        var rows = LoadoutPlugins(loadout);

        rows.Select(r => r.Name).Should().BeEquivalentTo(VanillaMasters, "only plugins directly in the data folder count");
        rows.Should().OnlyContain(r => r.IsEnabled && r.ModGroupId == null);
    }

    [Fact]
    public async Task LoadoutPlugins_AModWithTwoPlugins_AddsTwoRowsOwnedByTheMod()
    {
        var loadout = await VanillaLoadout();
        var mod = await AddMod(loadout, "Two Plugins", "Data/Alpha.esp", "Data/Beta.esl", "Data/Readme.txt");

        var rows = LoadoutPlugins(loadout);

        rows.Should().HaveCount(VanillaMasters.Length + 2);
        rows.Where(r => r.ModGroupId == mod).Select(r => r.Name).Should().BeEquivalentTo("Alpha.esp", "Beta.esl");
        rows.Where(r => r.ModGroupId == mod).Should().OnlyContain(r => r.IsEnabled && r.ModName == "Two Plugins");
    }

    [Fact]
    public async Task LoadoutPlugins_ADeletedVanillaPlugin_IsNotListed()
    {
        var loadout = await VanillaLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "Overrides");
            AddDeletedFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, "Data/Dawnguard.esm"));
            await tx.Commit();
        }

        var rows = LoadoutPlugins(loadout);

        rows.Select(r => r.Name).Should().BeEquivalentTo(VanillaMasters.Where(m => m != "Dawnguard.esm"));
    }

    private static void AddDeletedFile(ITransaction tx, LoadoutId loadoutId, LoadoutItemGroupId groupId, GamePath path)
    {
        _ = new DeletedFile.New(tx, out var id)
        {
            Reason = "Deleted from disk",
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                TargetPath = path.ToGamePathParentTuple(loadoutId),
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    LoadoutId = loadoutId,
                    ParentId = groupId,
                    Name = path.Path,
                },
            },
        };
    }

    [Fact]
    public async Task LoadoutPlugins_AModShippingItsOwnSkyrimEsm_WinsTheRowOverTheGameFile()
    {
        var loadout = await VanillaLoadout();
        var mod = await AddMod(loadout, "Patched Master", "Data/Skyrim.esm");

        var rows = LoadoutPlugins(loadout);

        var row = rows.Should().ContainSingle(r => r.Name == "Skyrim.esm").Subject;
        row.ModGroupId.Should().Be(mod);
        row.ModName.Should().Be("Patched Master");
    }

    [Fact]
    public async Task LoadoutPlugins_NamesCompareIgnoringCase()
    {
        var loadout = await VanillaLoadout();
        var mod = await AddMod(loadout, "Lowercase", "Data/skyrim.ESM");

        var rows = LoadoutPlugins(loadout);

        rows.Should().ContainSingle(r => r.Name.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase))
            .Which.ModGroupId.Should().Be(mod);
    }

    [Fact]
    public async Task LoadoutPlugins_TwoModsShippingTheSamePlugin_ProduceOneRowOwnedByTheNewerMod()
    {
        var loadout = await VanillaLoadout();
        await AddMod(loadout, "Older", "Data/Shared.esp");
        var newer = await AddMod(loadout, "Newer", "Data/Shared.esp");

        var rows = LoadoutPlugins(loadout);

        var row = rows.Should().ContainSingle(r => r.Name == "Shared.esp").Subject;
        row.ModGroupId.Should().Be(newer);
        row.ModName.Should().Be("Newer");
    }

    [Fact]
    public async Task LoadoutPlugins_AnEnabledModBeatsANewerDisabledOne()
    {
        var loadout = await VanillaLoadout();
        var enabled = await AddMod(loadout, "Enabled", "Data/Shared.esp");
        var disabled = await AddMod(loadout, "Disabled", "Data/Shared.esp");
        await DisableItem(disabled);

        var row = LoadoutPlugins(loadout).Should().ContainSingle(r => r.Name == "Shared.esp").Subject;

        row.ModGroupId.Should().Be(enabled);
        row.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task LoadoutPlugins_ADisabledModsPlugin_IsReportedDisabled()
    {
        var loadout = await VanillaLoadout();
        var mod = await AddMod(loadout, "Off", "Data/Off.esp");
        await DisableItem(mod);

        var row = LoadoutPlugins(loadout).Should().ContainSingle(r => r.Name == "Off.esp").Subject;

        row.IsEnabled.Should().BeFalse();
        row.ModGroupId.Should().Be(mod);
    }

    [Fact]
    public async Task SortOrderItems_SplitIntoLiveAndTombstones_ByWhetherTheLoadoutStillHasThePlugin()
    {
        var loadout = await VanillaLoadout();
        await AddMod(loadout, "Present", "Data/Present.esp");

        using var tx = Connection.BeginTransaction();
        var baseOrder = new LoadoutSortOrder.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            ParentEntity = loadout.LoadoutId,
            SortOrderTypeId = Guid.NewGuid(),
        };
        var sortOrder = new PluginSortOrder.New(tx, baseOrder)
        {
            SortOrder = baseOrder,
            IsMarker = true,
            SeededFromGameFile = false,
        };
        AddSortItem(tx, baseOrder.Id, "Skyrim.esm", 0);
        AddSortItem(tx, baseOrder.Id, "present.ESP", 1);
        AddSortItem(tx, baseOrder.Id, "Removed.esp", 2);
        var result = await tx.Commit();
        var id = result[baseOrder.Id];

        var all = Connection.Query<(string PluginName, int SortIndex)>($"SELECT PluginName, SortIndex FROM plugins.SortOrderItems({Connection}, {id.Value}) ORDER BY SortIndex").ToList();
        var live = Connection.Query<(string PluginName, int SortIndex)>(
            $"SELECT PluginName, SortIndex FROM plugins.LiveSortOrderItems({Connection}, {id.Value}, {loadout.LoadoutId}, {LocationId.Game.Value}, {DataFolder}) ORDER BY SortIndex").ToList();
        var tombstones = Connection.Query<(string PluginName, int SortIndex)>(
            $"SELECT PluginName, SortIndex FROM plugins.Tombstones({Connection}, {id.Value}, {loadout.LoadoutId}, {LocationId.Game.Value}, {DataFolder}) ORDER BY SortIndex").ToList();

        all.Select(r => r.PluginName).Should().Equal("Skyrim.esm", "present.ESP", "Removed.esp");
        live.Select(r => r.PluginName).Should().Equal("Skyrim.esm", "present.ESP");
        tombstones.Select(r => r.PluginName).Should().Equal("Removed.esp");
    }

    private static void AddSortItem(ITransaction tx, SortOrderId sortOrder, string name, int index)
    {
        var item = new SortOrderItem.New(tx) { ParentSortOrderId = sortOrder, SortIndex = index };
        _ = new PluginSortOrderItem.New(tx, item)
        {
            SortOrderItem = item,
            PluginName = name,
            IsEnabled = true,
        };
    }

    [Fact]
    public async Task LoadoutPlugins_AGameInstalledPlugin_IsListedAsGameInstalledAndFollowsTheGroupSwitch()
    {
        var loadout = await VanillaLoadout();
        var (group, item) = await Connection.AddGameInstalledPlugin(loadout, "Creation Title", "Data/ccnew.esl");

        var row = LoadoutPlugins(loadout).Single(r => r.Name == "ccnew.esl");
        row.IsGameInstalled.Should().BeTrue();
        row.ModName.Should().Be("Creation Title");
        row.ModGroupId.Should().Be(item);
        row.IsEnabled.Should().BeTrue();

        await DisableItem(group);
        LoadoutPlugins(loadout).Single(r => r.Name == "ccnew.esl").IsEnabled.Should().BeFalse("the group is switched off");

        await EnableItem(group);
        LoadoutPlugins(loadout).Single(r => r.Name == "ccnew.esl").IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task LoadoutPlugins_OnlyGameInstalledRowsAreMarkedGameInstalled()
    {
        var loadout = await VanillaLoadout();
        await AddMod(loadout, "Mod", "Data/Alpha.esp");
        await Connection.AddGameInstalledPlugin(loadout, "Creation Title", "Data/ccnew.esl");

        LoadoutPlugins(loadout).Where(r => r.IsGameInstalled).Select(r => r.Name).Should().Equal("ccnew.esl");
    }

    [Fact]
    public async Task LoadoutPlugins_TheLoadoutCopyOfAName_WinsOverTheGameInstalledOne()
    {
        var loadout = await VanillaLoadout();
        await Connection.AddGameInstalledPlugin(loadout, "Creation Title", "Data/Shared.esp");
        var mod = await AddMod(loadout, "Mod", "Data/Shared.esp");

        var row = LoadoutPlugins(loadout).Single(r => r.Name == "Shared.esp");
        row.ModGroupId.Should().Be(mod);
        row.IsGameInstalled.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadoutPlugins_TheGameInstalledCopy_BeatsTheVanillaRowWhateverTheGroupState(bool groupOff)
    {
        // A depot-shipped Creation has a vanilla row, always enabled, and a game-installed row that follows the
        // group. The game-installed row has to win either way, or the page would show an unlocked active plugin.
        var loadout = await CreateLoadout();
        await SeedGameFiles(loadout, [.. VanillaMasters.Select(m => $"{DataFolder}/{m}"), "Data/ccdepot.esl"]);
        var (group, _) = await Connection.AddGameInstalledPlugin(loadout, "Depot Creation", "Data/ccdepot.esl");
        if (groupOff) await DisableItem(group);

        var rows = LoadoutPlugins(loadout).Where(r => r.Name == "ccdepot.esl").ToList();
        rows.Should().ContainSingle();
        rows[0].IsGameInstalled.Should().BeTrue();
        rows[0].IsEnabled.Should().Be(!groupOff);
    }

    [Fact]
    public async Task LiveSortOrderItems_CarryIsGameInstalled_AndFollowAToggleOfTheGroup()
    {
        var loadout = await VanillaLoadout();
        var (group, _) = await Connection.AddGameInstalledPlugin(loadout, "Creation Title", "Data/ccnew.esl");
        var variety = new PluginSortOrderVariety(ServiceProvider, Game);
        var sortOrder = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await variety.ReconcileSortOrder(sortOrder);

        (string PluginName, bool LoadoutIsEnabled, bool IsGameInstalled) Row() =>
            Connection.Query<(string PluginName, bool LoadoutIsEnabled, bool IsGameInstalled)>(
                    $"SELECT PluginName, LoadoutIsEnabled, IsGameInstalled FROM plugins.LiveSortOrderItems({Connection}, {sortOrder.Value}, {loadout.LoadoutId}, {LocationId.Game.Value}, {DataFolder})")
                .Single(r => r.PluginName == "ccnew.esl");

        Row().Should().Be(("ccnew.esl", true, true));

        await DisableItem(group);
        Row().Should().Be(("ccnew.esl", false, true));
    }
}

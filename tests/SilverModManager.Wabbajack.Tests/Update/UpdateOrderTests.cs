using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Acquire;
using SilverModManager.Wabbajack.Tests.Install;
using SilverModManager.Wabbajack.Update;

namespace SilverModManager.Wabbajack.Tests.Update;

/// <summary>The order and switches of an updated list: the old profile is the base, the new profile and the user's loadout are the two sides.</summary>
public class UpdateOrderTests(ITestOutputHelper helper) : InstallTestBase<UpdateOrderTests>(helper)
{
    private static readonly InstallOptions Options = new(false, new HashSet<RelativePath>());

    private readonly ListLogger<InstallModlistJob> _log = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<ILogger<InstallModlistJob>>(_log);

    private IPluginSortOrderVariety Variety => Game.SortOrderManager.GetSortOrderVarieties().OfType<IPluginSortOrderVariety>().Single();

    /// <summary>A list with a five-file folder per mod name, so a folder can be recognised again under another name.</summary>
    private async Task<WabbajackModlistLibraryFile.ReadOnly> AddList(string modlistTxt, string[] mods, string? loadorderTxt = null, string? pluginsTxt = null)
    {
        var list = new ListBuilder();
        foreach (var mod in mods)
            for (var i = 0; i < 5; i++)
                list.Directives.Add(list.Inline($"mods/{mod}/f{i}.txt", $"{mod} file {i}"));
        if (loadorderTxt is not null || pluginsTxt is not null)
            foreach (var mod in mods) list.Directives.Add(list.Inline($"mods/{mod}/{mod}.esp", Plugin()));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", modlistTxt));
        if (loadorderTxt is not null) list.Directives.Add(list.Inline("profiles/Default/loadorder.txt", loadorderTxt));
        if (pluginsTxt is not null) list.Directives.Add(list.Inline("profiles/Default/plugins.txt", pluginsTxt));
        return await AddModlist(list.ToList("Test list"), list.Blobs);
    }

    private static InstallOptions UpdateOf(WabbajackModlistLoadoutGroup.ReadOnly group)
        => new(false, new HashSet<RelativePath>(), group.WabbajackModlistLoadoutGroupId);

    private WabbajackModlistLoadoutGroup.ReadOnly Fresh(WabbajackModlistLoadoutGroup.ReadOnly group)
        => WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id);

    private async Task<WabbajackModlistLoadoutGroup.ReadOnly> Update(Loadout.ReadOnly loadout, WabbajackModlistLibraryFile.ReadOnly file, WabbajackModlistLoadoutGroup.ReadOnly group)
        => (await Install(loadout, file, UpdateOf(group))).Group;

    private LoadoutItemGroup.ReadOnly ModGroup(WabbajackModlistLoadoutGroup.ReadOnly group, string name)
        => LoadoutItem.FindByParent(Connection.Db, group.Id).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == name);

    private bool IsOff(WabbajackModlistLoadoutGroup.ReadOnly group, string name)
        => LoadoutItem.Load(Connection.Db, ModGroup(group, name).Id).IsDisabled;

    /// <summary>The mod names, winner first.</summary>
    private string[] OrderOf(WabbajackModlistLoadoutGroup.ReadOnly group)
        => ContainerOrder.ChildrenWinnerFirst(Connection.Db, group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId)
            .Select(c => c.AsLoadoutItem().Name).ToArray();

    private async Task UserSets(WabbajackModlistLoadoutGroup.ReadOnly group, Loadout.ReadOnly loadout, params string[] winnerToLoser)
    {
        using var tx = Connection.BeginTransaction();
        LoadoutManager.SetGroupOrder(loadout.LoadoutId, winnerToLoser.Select(n => ModGroup(group, n).LoadoutItemGroupId).ToArray(), tx);
        await tx.Commit();
    }

    private async Task UserSwitchesOff(WabbajackModlistLoadoutGroup.ReadOnly group, string name)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(ModGroup(group, name).Id, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    [Fact]
    public async Task AKeptModTheUserSwitchedOffStaysOffWhenTheListDidNotChangeIt()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n+B\n", ["A", "B"]);
        var two = await AddList("+A\n+B\n+C\n", ["A", "B", "C"]);
        var (_, group) = await Install(loadout, one, Options);
        await UserSwitchesOff(group, "A");

        var updated = await Update(loadout, two, group);

        IsOff(updated, "A").Should().BeTrue("the list left A's switch alone, so the user's stays");
        IsOff(updated, "B").Should().BeFalse();
    }

    [Fact]
    public async Task AKeptModWhoseFlagTheListFlippedTakesTheNewFlagWhenTheUserLeftIt()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n+B\n", ["A", "B"]);
        var two = await AddList("+A\n-B\n+C\n", ["A", "B", "C"]);
        var (_, group) = await Install(loadout, one, Options);

        var updated = await Update(loadout, two, group);

        IsOff(updated, "B").Should().BeTrue("the list switched B off and the user had not touched it");
        IsOff(updated, "A").Should().BeFalse();
    }

    [Fact]
    public async Task AKeptModBothTheUserAndTheListFlippedKeepsThatFlagAndIsNotListedInThePreview()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n+B\n", ["A", "B"]);
        var two = await AddList("+A\n-B\n+C\n", ["A", "B", "C"]);
        var (_, group) = await Install(loadout, one, Options);
        await UserSwitchesOff(group, "B");

        var preview = await new ModlistUpdatePreview(ServiceProvider).Preview(Fresh(group), two, CancellationToken.None);
        var updated = await Update(loadout, two, group);

        IsOff(updated, "B").Should().BeTrue("user and list agree");
        preview.Conflicts.Should().BeEmpty("a switch both sides flipped to the same value is not an override");
        preview.ModOrder.Should().NotContain("B", "the list changes nothing about B's switch");
    }

    [Fact]
    public async Task AModTheListInsertedAtTheTopMovesNothingTheUserPlaced()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n+B\n+C\n", ["A", "B", "C"]);
        var two = await AddList("+X\n+A\n+B\n+C\n", ["A", "B", "C", "X"]);
        var (_, group) = await Install(loadout, one, Options);
        await UserSets(group, loadout, "C", "A", "B");

        var updated = await Update(loadout, two, group);

        var order = OrderOf(updated);
        order.Where(n => n != "X").Should().Equal("C", "A", "B");
        order.Should().Equal(["C", "X", "A", "B"], "the list put X right before A and the user never placed it");
        _log.Messages(LogLevel.Information).Should().NotContain(m => m.Contains("left X as the user had it"));
    }

    [Fact]
    public async Task AModTheListInsertedMidOrderGoesAtItsListedSlotAfterTheUserReorderedTheOthers()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n+B\n+C\n", ["A", "B", "C"]);
        var two = await AddList("+A\n+X\n+B\n+C\n", ["A", "B", "C", "X"]);
        var (_, group) = await Install(loadout, one, Options);
        await UserSets(group, loadout, "C", "A", "B");

        var updated = await Update(loadout, two, group);

        var order = OrderOf(updated);
        order.Where(n => n != "X").Should().Equal("C", "A", "B");
        order.Should().Equal("C", "A", "X", "B");
        _log.Messages(LogLevel.Information).Should().NotContain(m => m.Contains("left X as the user had it"));
    }

    [Fact]
    public async Task AnUpdateResumedAfterARenameStillUsesTheRenamedModsOldPositionAsTheBase()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n+B\n+C\n", ["A", "B", "C"]);
        var list = new ListBuilder();
        foreach (var mod in new[] { "B", "C" })
            for (var i = 0; i < 5; i++) list.Directives.Add(list.Inline($"mods/{mod}/f{i}.txt", $"{mod} file {i}"));
        for (var i = 0; i < 5; i++) list.Directives.Add(list.Inline($"mods/A SE/f{i}.txt", $"A file {i}"));
        for (var i = 0; i < 8; i++) list.Directives.Add(list.Inline($"mods/D/d{i}.txt", $"D file {i}"));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", "+A SE\n+B\n+C\n+D\n"));
        var two = await AddModlist(list.ToList("Test list"), list.Blobs);
        var (_, group) = await Install(loadout, one, Options);
        await UserSets(group, loadout, "B", "C", "A");

        var failing = new UpdateJobTests.FailingAfter(FileStore, 1);
        var job = NewJob(loadout, two, UpdateOf(group), writer: new GeneratedFileWriter(failing, TemporaryFileManager), parallelism: 1, commits: new CommitPolicy(2, long.MaxValue, TimeSpan.MaxValue));
        var failure = await Record.ExceptionAsync(() => Run(job));
        failure.Should().BeOfType<IOException>();
        ModGroup(Fresh(group), "A SE").Should().NotBeNull("the rename was committed before the run stopped");

        var (_, resumed) = await Install(loadout, two, Options);

        OrderOf(resumed).Where(n => n != "D").Should().Equal("B", "C", "A SE");
        _log.Messages(LogLevel.Information).Should().NotContain(m => m.Contains("left A SE as the user had it"));
    }

    [Fact]
    public async Task TwoKeptPluginsTheUserReorderedKeepTheirOrder()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n+B\n", ["A", "B"], "A.esp\nB.esp\n", "*A.esp\n*B.esp\n");
        var two = await AddList("+A\n+B\n+C\n", ["A", "B", "C"], "A.esp\nB.esp\nC.esp\n", "*A.esp\n*B.esp\n*C.esp\n");
        var (_, group) = await Install(loadout, one, Options);
        var sortOrder = Variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).Value;
        using (var tx = Connection.BeginTransaction())
        {
            Variety.StageOrder(sortOrder, ["B.esp", "A.esp"], tx);
            await tx.Commit();
        }

        await Update(loadout, two, group);

        // The list puts C after B; the user's B-before-A stays.
        Variety.StoredOrder(Refresh(loadout)).Select(r => r.Name).Should().Equal("B.esp", "C.esp", "A.esp");
    }

    [Fact]
    public async Task APluginNewInTheNewProfileIsInsertedWhereTheProfilePutsIt()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n+B\n", ["A", "B"], "A.esp\nB.esp\n", "*A.esp\n*B.esp\n");
        var two = await AddList("+A\n+B\n+N\n", ["A", "B", "N"], "A.esp\nN.esp\nB.esp\n", "*A.esp\n*N.esp\n*B.esp\n");
        var (_, group) = await Install(loadout, one, Options);

        await Update(loadout, two, group);

        Variety.StoredOrder(Refresh(loadout)).Select(r => r.Name).Should().Equal("A.esp", "N.esp", "B.esp");
    }

    [Fact]
    public async Task ARenamedModsOldPositionIsUsedAsTheThreeWayBase()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n+B\n+C\n", ["A", "B", "C"]);
        // The same five files under another folder name, in the same place in the list.
        var list = new ListBuilder();
        foreach (var mod in new[] { "B", "C" })
            for (var i = 0; i < 5; i++) list.Directives.Add(list.Inline($"mods/{mod}/f{i}.txt", $"{mod} file {i}"));
        for (var i = 0; i < 5; i++) list.Directives.Add(list.Inline($"mods/A SE/f{i}.txt", $"A file {i}"));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", "+A SE\n+B\n+C\n"));
        var two = await AddModlist(list.ToList("Test list"), list.Blobs);
        var (_, group) = await Install(loadout, one, Options);
        await UserSets(group, loadout, "B", "C", "A");
        await UserSwitchesOff(group, "A");

        var preview = await new ModlistUpdatePreview(ServiceProvider).Preview(Fresh(group), two, CancellationToken.None);
        preview.ModOrder.Should().BeEmpty("the renamed mod is where the list leaves it and the user moved it");
        preview.Conflicts.Should().BeEmpty();

        var updated = await Update(loadout, two, group);

        ModGroup(updated, "A SE").Should().NotBeNull();
        OrderOf(updated).Should().Equal("B", "C", "A SE");
        IsOff(updated, "A SE").Should().BeTrue("the list never flipped it, so the user's switch stays");
    }

    [Fact]
    public async Task AnUpdateWithTheOldFileGoneAppliesTheFullOrderAndWarns()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n+B\n", ["A", "B"]);
        var two = await AddList("+B\n+A\n-C\n", ["A", "B", "C"]);
        var (_, group) = await Install(loadout, one, Options);
        await UserSwitchesOff(group, "A");
        one.AsLibraryFile().TryGetAsLibraryArchive(out var archive).Should().BeTrue();
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var child in archive.Children) tx.Delete(child.Id, recursive: false);
            await tx.Commit();
        }

        var updated = await Update(loadout, two, group);

        OrderOf(updated).Should().Equal("B", "A", "C");
        IsOff(updated, "A").Should().BeFalse("without the old profile the list's order and flags are applied in full");
        IsOff(updated, "C").Should().BeTrue();
        _log.Messages(LogLevel.Warning).Should().Contain(m => m.Contains(UpdateOrderPlanner.InstalledVersionGoneWarning));
    }
}

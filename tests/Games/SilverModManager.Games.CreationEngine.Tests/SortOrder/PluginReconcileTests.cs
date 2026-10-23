using FluentAssertions;
using NexusMods.Games.CreationEngine.SortOrder;
using NexusMods.Hashing.xxHash3;

namespace NexusMods.Games.CreationEngine.Tests.SortOrder;

/// <summary>
/// How the plugin variety merges the stored order with what the loadout offers, without a database.
/// </summary>
public class PluginReconcileTests
{
    private static PluginSortData Row(string name, int index, bool enabled = true, int? early = null) => new(name, index, enabled, early);

    private static PluginLoadoutData Plugin(string name, bool master = false, int? early = null, string[]? masters = null) =>
        new(name, Hash.From(1), true, "Mod", null, master, masters ?? []) { EarlyLoaderIndex = early };

    private static string[] Order(IEnumerable<(PluginSortData SortedEntry, PluginLoadoutData ItemLoadoutData)> result) =>
        result.OrderBy(r => r.SortedEntry.SortIndex).Select(r => r.SortedEntry.Name).ToArray();

    private static PluginSortData? NoTombstones(string key) => null;

    [Fact]
    public void ExistingEntries_KeepTheirRelativeOrderAndAreIndexedFromZero()
    {
        var result = PluginSortOrderVariety.Reconcile(
            [Row("b.esp", 7), Row("a.esp", 9), Row("c.esp", 12)],
            [Plugin("a.esp"), Plugin("b.esp"), Plugin("c.esp")],
            NoTombstones);

        Order(result).Should().Equal("b.esp", "a.esp", "c.esp");
        result.Select(r => r.SortedEntry.SortIndex).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void NewPlugins_GoAtTheEnd_MastersFirstThenByName()
    {
        var result = PluginSortOrderVariety.Reconcile(
            [Row("old.esp", 0)],
            [Plugin("old.esp"), Plugin("zeta.esp"), Plugin("alpha.esp"), Plugin("Core.esm", master: true)],
            NoTombstones);

        Order(result).Should().Equal("old.esp", "Core.esm", "alpha.esp", "zeta.esp");
        result.Skip(1).Should().OnlyContain(r => r.SortedEntry.IsEnabled, "a new plugin starts switched on");
    }

    [Fact]
    public void NewPlugins_FollowTheirMastersEvenWhenTheNameSortsFirst()
    {
        var result = PluginSortOrderVariety.Reconcile(
            [],
            [Plugin("A.esp", masters: ["Z.esp"]), Plugin("Z.esp"), Plugin("Core.esm", master: true)],
            NoTombstones);

        Order(result).Should().Equal("Core.esm", "Z.esp", "A.esp");
    }

    [Fact]
    public void NewPlugins_ThatListEachOtherAsMasters_StillGetAnOrderSoThePageStaysUsable()
    {
        var result = PluginSortOrderVariety.Reconcile(
            [],
            [Plugin("B.esm", master: true, masters: ["A.esm"]), Plugin("A.esm", master: true, masters: ["B.esm"]), Plugin("C.esp")],
            NoTombstones);

        Order(result).Should().Equal("A.esm", "B.esm", "C.esp");
    }

    [Fact]
    public void AnEntryWithoutLoadoutData_IsNotInTheResult()
    {
        var result = PluginSortOrderVariety.Reconcile([Row("gone.esp", 0), Row("here.esp", 1)], [Plugin("here.esp")], NoTombstones);

        Order(result).Should().Equal("here.esp");
    }

    [Fact]
    public void ATombstone_ComesBackAtItsStoredIndexWithItsSwitch()
    {
        var tombstones = new Dictionary<string, PluginSortData> { ["mid.esp"] = Row("mid.esp", 1, enabled: false) };

        var result = PluginSortOrderVariety.Reconcile(
            [Row("a.esp", 0), Row("b.esp", 1), Row("c.esp", 2)],
            [Plugin("a.esp"), Plugin("b.esp"), Plugin("c.esp"), Plugin("Mid.esp")],
            key => tombstones.GetValueOrDefault(key));

        Order(result).Should().Equal("a.esp", "Mid.esp", "b.esp", "c.esp");
        result.Single(r => r.SortedEntry.Name == "Mid.esp").SortedEntry.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void ATombstoneStoredBeyondTheEnd_ComesBackLast()
    {
        var tombstones = new Dictionary<string, PluginSortData> { ["far.esp"] = Row("far.esp", 40) };

        var result = PluginSortOrderVariety.Reconcile(
            [Row("a.esp", 0)],
            [Plugin("a.esp"), Plugin("far.esp")],
            key => tombstones.GetValueOrDefault(key));

        Order(result).Should().Equal("a.esp", "far.esp");
    }

    [Fact]
    public void ANewEarlyLoader_JoinsTheLockedRowsAtTheTopInGameOrder()
    {
        var result = PluginSortOrderVariety.Reconcile(
            [Row("Skyrim.esm", 0, early: 0), Row("Dragonborn.esm", 1, early: 4), Row("a.esp", 2)],
            [Plugin("Skyrim.esm", early: 0), Plugin("Update.esm", early: 1), Plugin("Dragonborn.esm", early: 4), Plugin("a.esp")],
            NoTombstones);

        Order(result).Should().Equal("Skyrim.esm", "Update.esm", "Dragonborn.esm", "a.esp");
    }

    [Fact]
    public void AStoredRowThatBecameAnEarlyLoader_MovesUpIntoTheLockedRows()
    {
        // A game update can add a plugin the order already holds to the game's Creation Club list.
        var result = PluginSortOrderVariety.Reconcile(
            [Row("Skyrim.esm", 0, early: 0), Row("a.esp", 1), Row("ccFish.esm", 2, early: 1), Row("b.esp", 3)],
            [Plugin("Skyrim.esm", early: 0), Plugin("a.esp"), Plugin("ccFish.esm", early: 1), Plugin("b.esp")],
            NoTombstones);

        Order(result).Should().Equal("Skyrim.esm", "ccFish.esm", "a.esp", "b.esp");
    }

    [Fact]
    public void ATombstoneStoredAmongTheEarlyLoaders_ComesBackBelowThem()
    {
        // Stored at index 1 before a second early loader took that slot.
        var tombstones = new Dictionary<string, PluginSortData> { ["back.esp"] = Row("back.esp", 1) };

        var result = PluginSortOrderVariety.Reconcile(
            [Row("Skyrim.esm", 0, early: 0), Row("a.esp", 1)],
            [Plugin("Skyrim.esm", early: 0), Plugin("Update.esm", early: 1), Plugin("a.esp"), Plugin("back.esp")],
            key => tombstones.GetValueOrDefault(key));

        Order(result).Should().Equal("Skyrim.esm", "Update.esm", "back.esp", "a.esp");
    }

    [Fact]
    public void AFreshLoadout_StartsWithTheEarlyLoadersInGameOrder()
    {
        var result = PluginSortOrderVariety.Reconcile(
            [],
            [Plugin("Mod.esp"), Plugin("Update.esm", master: true, early: 1), Plugin("Skyrim.esm", master: true, early: 0), Plugin("Other.esm", master: true)],
            NoTombstones);

        Order(result).Should().Equal("Skyrim.esm", "Update.esm", "Other.esm", "Mod.esp");
    }

    [Fact]
    public void ARowThatIsNotStored_RefusesToGiveAStoredRowId()
    {
        var act = () => Row("new.esp", 0).StoredRowId;

        act.Should().Throw<InvalidOperationException>().WithMessage("*new.esp*");
    }

    [Fact]
    public void TheOnDiskSpellingFollowsTheLoadout()
    {
        var result = PluginSortOrderVariety.Reconcile([Row("mod.esp", 0)], [Plugin("Mod.esp")], NoTombstones);

        result.Single().SortedEntry.Name.Should().Be("Mod.esp");
    }
}

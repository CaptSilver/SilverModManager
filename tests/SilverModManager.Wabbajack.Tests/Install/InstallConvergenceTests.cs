using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Install;
using NexusMods.Wabbajack.Models;

namespace NexusMods.Wabbajack.Tests.Install;

/// <summary>Installing a list again, after the list changed, leaves the loadout matching the new list.</summary>
public class InstallConvergenceTests(ITestOutputHelper helper) : InstallTestBase<InstallConvergenceTests>(helper)
{
    private static readonly InstallOptions Options = new(false, new HashSet<RelativePath>());

    private static ListBuilder List(string modlistTxt, params (string To, string Text)[] files)
    {
        var list = new ListBuilder();
        foreach (var (to, text) in files) list.Directives.Add(list.Inline(to, text));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", modlistTxt));
        return list;
    }

    /// <summary>Installs <paramref name="first"/>, then points the same group at <paramref name="second"/> and installs that, as an updated list would be.</summary>
    private async Task<(Loadout.ReadOnly Loadout, WabbajackModlistLoadoutGroup.ReadOnly First, WabbajackModlistLoadoutGroup.ReadOnly Second)> Reinstall(ListBuilder first, ListBuilder second)
    {
        var loadout = await CreateLoadout();
        var (_, installed) = await Install(loadout, await AddModlist(first.ToList(), first.Blobs), Options);

        var updated = await AddModlist(second.ToList(), second.Blobs);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(installed.Id, WabbajackModlistLoadoutGroup.Modlist, updated.Id);
            await tx.Commit();
        }
        var (_, again) = await Install(loadout, updated, Options);
        return (loadout, installed, again);
    }

    [Fact]
    public async Task AFileWhoseContentChangedReplacesItsOldRowAtThePath()
    {
        var (_, first, second) = await Reinstall(
            List("+A\n", ("mods/A/a.txt", "one")),
            List("+A\n", ("mods/A/a.txt", "two")));

        second.Id.Should().Be(first.Id);
        var files = TreeOf(second)["A"];
        files.Keys.Should().BeEquivalentTo("Game/Data/a.txt");
        files["Game/Data/a.txt"].Should().Be("two"u8.ToArray().xxHash3());
        LoadoutFile.All(Connection.Db).Count(f => f.AsLoadoutItemWithTargetPath().AsLoadoutItem().Name == "a.txt").Should().Be(1);
    }

    [Fact]
    public async Task AFileTheListNoLongerInstallsIsRemovedFromItsGroup()
    {
        var (_, _, second) = await Reinstall(
            List("+A\n", ("mods/A/a.txt", "one"), ("mods/A/b.txt", "bee")),
            List("+A\n", ("mods/A/a.txt", "one")));

        TreeOf(second)["A"].Keys.Should().BeEquivalentTo("Game/Data/a.txt");
    }

    [Fact]
    public async Task AFileTheUpdatedListCannotProduceKeepsTheRowAnEarlierRunInstalled()
    {
        var archive = await AddZip("a.zip", new() { ["base.bin"] = [1, 2, 3], ["tex.dds"] = Bc1Dds(8) });
        var first = List("+A\n", ("mods/A/x.bin", "one"));
        var second = List("+A\n");
        second.Archives.Add(archive);
        second.Directives.Add(UnwritableTexture("mods/A/x.bin", archive, "tex.dds"));

        var (_, _, again) = await Reinstall(first, second);

        SkipsOf(again).Should().ContainSingle(s => s.Kind == SkipKind.Transform);
        var files = TreeOf(again)["A"];
        files.Keys.Should().BeEquivalentTo("Game/Data/x.bin");
        files["Game/Data/x.bin"].Should().Be("one"u8.ToArray().xxHash3());
    }

    [Fact]
    public async Task AGroupTheProfileTurnedFromOffToOnIsEnabledAgain()
    {
        var (_, _, second) = await Reinstall(
            List("-A\n+B\n", ("mods/A/a.txt", "a"), ("mods/B/b.txt", "b")),
            List("+A\n+B\n", ("mods/A/a.txt", "a"), ("mods/B/b.txt", "b")));

        LoadoutItem.FindByParent(Connection.Db, second.Id).OfTypeLoadoutItemGroup()
            .Where(g => g.AsLoadoutItem().IsDisabled).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallingAgainKeepsTheListBelowModsInstalledAfterTheFirstRun()
    {
        var list = List("+B\n+A\n", ("mods/A/a.txt", "a"), ("mods/B/b.txt", "b"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var (_, first) = await Install(loadout, modlist, Options);
        var before = PrioritiesOf(first);

        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [P("Data/later.txt")] = "later"u8.ToArray() });
        var library = await RegisterLocalArchive(archive.Path);
        var later = (await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId)).LoadoutItemGroup!.Value.LoadoutItemGroupId;
        var laterPriority = LoadoutItemGroupPriority.FindByTarget(Connection.Db, later).Single().Priority;

        var (_, second) = await Install(loadout, modlist, Options);

        PrioritiesOf(second).Should().BeEquivalentTo(before);
        PrioritiesOf(second).Values.Should().OnlyContain(p => p.Value < laterPriority.Value);
    }

    [Fact]
    public async Task AGroupAddedByTheUpdatedListTakesAPlaceAboveEveryExistingRowAndKeepsTheListsOrder()
    {
        var (_, first, second) = await Reinstall(
            List("+B\n+A\n", ("mods/A/a.txt", "a"), ("mods/B/b.txt", "b")),
            List("+C\n+B\n+A\n", ("mods/A/a.txt", "a"), ("mods/B/b.txt", "b"), ("mods/C/c.txt", "c")));

        var priorities = PrioritiesOf(second);
        priorities["C"].Value.Should().BeGreaterThan(priorities["B"].Value);
        priorities["B"].Value.Should().BeGreaterThan(priorities["A"].Value);
        priorities.Values.Select(p => p.Value).Distinct().Should().HaveCount(priorities.Count);
    }

    private Dictionary<string, ConflictPriority> PrioritiesOf(WabbajackModlistLoadoutGroup.ReadOnly group)
    {
        var db = Connection.Db;
        var result = new Dictionary<string, ConflictPriority>();
        foreach (var child in LoadoutItem.FindByParent(db, group.Id).OfTypeLoadoutItemGroup())
        {
            var rows = LoadoutItemGroupPriority.FindByTarget(db, child.LoadoutItemGroupId).ToArray();
            rows.Should().ContainSingle($"'{child.AsLoadoutItem().Name}' has exactly one priority row");
            result[child.AsLoadoutItem().Name] = rows[0].Priority;
        }
        return result;
    }
}

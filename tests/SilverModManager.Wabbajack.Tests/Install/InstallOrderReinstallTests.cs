using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>What a repeat install does to the order when a new revision of the list ships a mod the loadout has no priority row for yet.</summary>
public class InstallOrderReinstallTests(ITestOutputHelper helper) : InstallTestBase<InstallOrderReinstallTests>(helper)
{
    private static readonly InstallOptions Options = new(false, new HashSet<RelativePath>());

    /// <summary>
    /// The list with the mods B, A and C, listed in that order. Mod A comes from an archive the library does not hold
    /// until <see cref="AddArchiveOfModA"/> runs, so a first install that skips missing archives has no group for it and
    /// a later install of the same list creates it.
    /// </summary>
    private async Task<WabbajackModlistLibraryFile.ReadOnly> AddList()
    {
        var archiveOfA = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [P("a.txt")] = "a"u8.ToArray() });
        _archiveOfAPath = archiveOfA.Path;
        var bytes = await archiveOfA.Path.ReadAllBytesAsync();
        var entry = new ArchiveEntry(Hash64(bytes), bytes.Length, "a.zip", "", new HttpState(new Uri("https://example.invalid/a.zip"), []));

        var list = new ListBuilder();
        var exe = GameExeArchive();
        list.Archives.AddRange([exe, entry]);
        list.Directives.AddRange(
        [
            new FromArchive(P("mods/A/a.txt"), Hash64("a"u8.ToArray()), 1, new ArchiveHashPath(entry.XxHash64, [P("a.txt")])),
            list.Inline("mods/B/b.txt", "b"),
            list.Inline("mods/C/c.txt", "c"),
            list.Inline("overwrite/over.txt", "over"),
            list.Inline("profiles/Default/modlist.txt", "+B\n+A\n+C\n"),
            list.Remapped("ModOrganizer.ini", "[General]\nselected_profile=@ByteArray(Default)\n"),
            new FromArchive(P("Stock Game/SkyrimSE.exe"), Hash64(StubExe), StubExe.Length, new ArchiveHashPath(exe.XxHash64, [])),
        ]);
        return await AddModlist(list.ToList(), list.Blobs);
    }

    private AbsolutePath _archiveOfAPath;

    private async Task AddArchiveOfModA() => await RegisterLocalArchive(_archiveOfAPath);

    private static readonly InstallOptions SkippingMissing = new(true, new HashSet<RelativePath>());

    private Dictionary<string, ulong> PrioritiesOf(WabbajackModlistLoadoutGroup.ReadOnly group)
    {
        var db = Connection.Db;
        return LoadoutItem.FindByParent(db, group.Id).OfTypeLoadoutItemGroup().ToDictionary(
            g => g.AsLoadoutItem().Name,
            g => LoadoutItemGroupPriority.FindByTarget(db, g.LoadoutItemGroupId).Single().Priority.Value);
    }

    private async Task<LoadoutItemGroupId> InstallOutsideMod(Loadout.ReadOnly loadout)
    {
        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [P("Data/outside.txt")] = "outside"u8.ToArray() });
        var library = await RegisterLocalArchive(archive.Path);
        var result = await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        return result.LoadoutItemGroup!.Value.LoadoutItemGroupId;
    }

    [Fact]
    public async Task AModTheListGainsOnARepeatInstallTakesItsListedPlaceAndAModInstalledAboveTheListStaysAboveOverwrite()
    {
        var loadout = await CreateLoadout();
        var modlist = await AddList();
        var (_, first) = await Install(loadout, modlist, SkippingMissing);
        PrioritiesOf(first).Keys.Should().NotContain("A");
        var outside = await InstallOutsideMod(loadout);

        await AddArchiveOfModA();
        var (_, group) = await Install(loadout, modlist, SkippingMissing);

        var priorities = PrioritiesOf(group);
        priorities.Keys.Should().Contain("A");
        priorities["B"].Should().BeGreaterThan(priorities["A"]);
        priorities["A"].Should().BeGreaterThan(priorities["C"], "the new mod sits where modlist.txt lists it, not on top");
        priorities["Overwrite"].Should().BeGreaterThan(priorities["B"]);
        var outsideNow = LoadoutItemGroupPriority.FindByTarget(Connection.Db, outside).Single().Priority.Value;
        outsideNow.Should().BeGreaterThan(priorities["Overwrite"], "a mod the user installed above the list stays above it");
    }

    [Fact]
    public async Task AStoredRuleTurningARoundTheListedOrderOfAModTheListGainsIsRecordedAndDoesNotFailTheInstall()
    {
        var loadout = await CreateLoadout();
        var modlist = await AddList();
        var (_, first) = await Install(loadout, modlist, SkippingMissing);
        // C beats B although the list puts B above C; the mod the repeat install adds sits between them.
        var added = await LoadoutManager.AddGroupRule(loadout.LoadoutId, GroupId(first, "C"), GroupId(first, "B"), GroupRuleKind.After);
        added.IsCycle.Should().BeFalse();

        await AddArchiveOfModA();
        var (_, group) = await Install(loadout, modlist, SkippingMissing);

        var dropped = ModOrderDroppedEntry.FindByLoadout(Connection.Db, loadout.LoadoutId).ToArray();
        dropped.Should().NotBeEmpty("the stored rule overrides the list's order");
        dropped.Should().OnlyContain(d => d.ContainerId == group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId);
        dropped.Should().OnlyContain(d => d.Item == "C wins over B", "the first install's note about the missing mod A is replaced");
        PrioritiesOf(group).Should().ContainKey("A");
    }

    private LoadoutItemGroupId GroupId(WabbajackModlistLoadoutGroup.ReadOnly group, string name) =>
        LoadoutItem.FindByParent(Connection.Db, group.Id).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == name).LoadoutItemGroupId;
}

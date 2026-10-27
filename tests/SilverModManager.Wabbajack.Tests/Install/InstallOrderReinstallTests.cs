using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Wabbajack.Format;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>What a repeat install does to the order when the list holds a group the loadout has no priority row for yet.</summary>
public class InstallOrderReinstallTests(ITestOutputHelper helper) : InstallTestBase<InstallOrderReinstallTests>(helper)
{
    private static readonly InstallOptions Options = new(false, new HashSet<RelativePath>());

    [Fact]
    public async Task AModThatGainedALineMidListTakesItsListedPlaceAndOverwriteStaysOnTop()
    {
        var list = new ListBuilder();
        var exe = GameExeArchive();
        list.Archives.Add(exe);
        list.Directives.AddRange(
        [
            list.Inline("mods/A/a.txt", "a"),
            list.Inline("mods/B/b.txt", "b"),
            list.Inline("mods/C/c.txt", "c"),
            list.Inline("overwrite/over.txt", "over"),
            list.Inline("profiles/Default/modlist.txt", "+B\n+A\n+C\n"),
            list.Remapped("ModOrganizer.ini", "[General]\nselected_profile=@ByteArray(Default)\n"),
            new FromArchive(P("Stock Game/SkyrimSE.exe"), Hash64(StubExe), StubExe.Length, new ArchiveHashPath(exe.XxHash64, [])),
        ]);
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var (_, group) = await Install(loadout, modlist, Options);

        // The mod the new revision added has a group but no priority row, as after any repeat install that gained a mod.
        var db = Connection.Db;
        var middle = LoadoutItem.FindByParent(db, group.Id).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == "A");
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var row in LoadoutItemGroupPriority.FindByTarget(db, middle.LoadoutItemGroupId)) tx.Delete(row.Id, recursive: false);
            await tx.Commit();
        }

        (_, group) = await Install(loadout, modlist, Options);

        db = Connection.Db;
        var priorities = LoadoutItem.FindByParent(db, group.Id).OfTypeLoadoutItemGroup().ToDictionary(
            g => g.AsLoadoutItem().Name,
            g => LoadoutItemGroupPriority.FindByTarget(db, g.LoadoutItemGroupId).Single().Priority.Value);
        priorities["B"].Should().BeGreaterThan(priorities["A"]);
        priorities["A"].Should().BeGreaterThan(priorities["C"], "the mod sits where modlist.txt lists it, not on top");
        priorities["Overwrite"].Should().BeGreaterThan(priorities["B"]);
    }
}

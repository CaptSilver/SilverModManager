using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Install;

namespace SilverModManager.Wabbajack.Tests.Library;

public class ModlistRemovalPlannerTests(ITestOutputHelper helper) : InstallTestBase<ModlistRemovalPlannerTests>(helper)
{
    private static readonly byte[] Bytes = "file"u8.ToArray();

    private ModlistRemovalPlanner Planner => ServiceProvider.GetRequiredService<ModlistRemovalPlanner>();

    private static InstallOptions Options => new(false, new HashSet<RelativePath>());

    private async Task<(Loadout.ReadOnly Loadout, WabbajackModlistLibraryFile.ReadOnly Modlist, WabbajackModlistLoadoutGroup.ReadOnly Group, EntityId Archive)> InstalledList(string name, ArchiveEntry archive)
    {
        var list = new ListBuilder();
        list.Archives.Add(archive);
        list.Directives.Add(list.Member("mods/A/f.bin", archive, "f.bin", Bytes));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", "+A\n"));
        var modlist = await AddModlist(list.ToList(name), list.Blobs);
        var file = ServiceProvider.GetRequiredService<ILibraryArchiveLocator>().FindTopLevelByXxHash64(Connection.Db, archive.XxHash64)!.Value.Id;
        await MarkFetched(modlist, file);

        var loadout = await CreateLoadout();
        var (_, group) = await Install(loadout, modlist, Options);
        return (loadout, modlist, group, file);
    }

    /// <summary>Records that the app fetched the archive for the list, as a gallery download would.</summary>
    private async Task MarkFetched(WabbajackModlistLibraryFile.ReadOnly list, EntityId file)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var row in WabbajackModlistArchive.FindByModlist(Connection.Db, list.Id))
            tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, file);
        await tx.Commit();
    }

    /// <summary>The link the install job writes, made by hand: the list's mod group for folder A points at the library file.</summary>
    private async Task LinkModGroup(WabbajackModlistLoadoutGroup.ReadOnly group, EntityId file)
    {
        var db = Connection.Db;
        var mod = LoadoutItem.FindByParent(db, group.Id).OfTypeLoadoutItemGroup()
            .Single(g => WabbajackModGroup.Folder.TryGetValue(g, out var folder) && folder == "A");
        using var tx = Connection.BeginTransaction();
        tx.Add(mod.Id, LibraryLinkedLoadoutItem.LibraryItem, file);
        await tx.Commit();
    }

    [Fact]
    public async Task AnArchiveLinkedOnlyByTheListsOwnModsIsStillOwned()
    {
        var archive = await AddZip("a.zip", new() { ["f.bin"] = Bytes });
        var (_, modlist, group, file) = await InstalledList("Test list", archive);
        await LinkModGroup(group, file);

        var plan = Planner.Plan(LibraryFileId.From(modlist.Id));

        plan.OwnedArchives.Should().Contain(LibraryItemId.From(file));
        plan.Kept.Should().BeEmpty();
    }

    [Fact]
    public async Task AnArchiveAUserModLinksIsKept()
    {
        var archive = await AddZip("a.zip", new() { ["f.bin"] = Bytes });
        var (loadout, modlist, group, file) = await InstalledList("Test list", archive);
        await LinkModGroup(group, file);
        await InstallByUser(loadout, file);

        var plan = Planner.Plan(LibraryFileId.From(modlist.Id));

        plan.OwnedArchives.Should().BeEmpty();
        plan.Kept.Should().ContainSingle().Which.Reason.Should().Be("installed in a loadout");
    }

    [Fact]
    public async Task AnArchiveAnotherListsModLinksIsKept()
    {
        var archive = await AddZip("a.zip", new() { ["f.bin"] = Bytes });
        var (_, modlist, group, file) = await InstalledList("Test list", archive);
        await LinkModGroup(group, file);
        var (_, _, otherGroup, _) = await InstalledList("Other list", archive);
        await LinkModGroup(otherGroup, file);

        var plan = Planner.Plan(LibraryFileId.From(modlist.Id));

        plan.OwnedArchives.Should().BeEmpty();
        plan.Kept.Should().ContainSingle().Which.Reason.Should().StartWith("used by");
    }

    [Fact]
    public async Task AnArchiveAnotherListsInstallLinksIsKeptEvenWhenNoOtherListNamesIt()
    {
        var archive = await AddZip("a.zip", new() { ["f.bin"] = Bytes });
        var (_, modlist, group, file) = await InstalledList("Test list", archive);
        await LinkModGroup(group, file);
        // A second install of the same list in another loadout, linked: the nearest collection is a list group of the same list, so it is still the list's own.
        var other = await CreateLoadout();
        var (_, otherGroup) = await Install(other, modlist, Options);
        await LinkModGroup(otherGroup, file);

        var plan = Planner.Plan(LibraryFileId.From(modlist.Id));

        plan.OwnedArchives.Should().Contain(LibraryItemId.From(file));
    }

    /// <summary>A mod the user installed from the archive into the loadout, outside any collection.</summary>
    private async Task InstallByUser(Loadout.ReadOnly loadout, EntityId file)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = "a.zip", LoadoutId = loadout.LoadoutId },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LoadoutItemGroup = group, LibraryItemId = LibraryItem.Load(Connection.Db, file) };
        await tx.Commit();
    }
}

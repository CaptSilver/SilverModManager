using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Install;

namespace SilverModManager.Wabbajack.Tests.Diagnostics;

public class ExtrasCleanupTests(ITestOutputHelper helper) : InstallTestBase<ExtrasCleanupTests>(helper)
{
    [Fact]
    public async Task RemovingTheCollectionDeletesItsExtrasFolderAndToolsAndLeavesASiblingsAlone()
    {
        var loadout = await CreateLoadout();
        var paths = ServiceProvider.GetRequiredService<WabbajackPaths>();
        var removed = await AddGroup(loadout, "Removed", withExtras: true);
        var kept = await AddGroup(loadout, "Kept", withExtras: true);
        foreach (var (name, group) in new[] { ("Removed", removed), ("Kept", kept) })
        {
            var folder = paths.ExtrasFor(name, group.Id);
            folder.Combine("tool").CreateDirectory();
            await folder.Combine("tool/tool.exe").WriteAllTextAsync("x");
            await AddTool(loadout, name, folder.Combine("tool/tool.exe").ToString());
        }
        await AddTool(loadout, "Unrelated", "/somewhere/else/tool.exe");

        await ServiceProvider.GetRequiredService<ILoadoutManager>().RemoveCollection(CollectionGroupId.From(removed.Id));

        await WaitUntil(() => !paths.ExtrasFor("Removed", removed.Id).Combine("tool/tool.exe").FileExists);
        await WaitUntil(() => ToolNames(loadout).All(n => n != "Removed"));
        paths.ExtrasFor("Kept", kept.Id).Combine("tool/tool.exe").FileExists.Should().BeTrue();
        ToolNames(loadout).Should().BeEquivalentTo(["Kept", "Unrelated"]);
        WabbajackModlistLoadoutGroup.Load(Connection.Db, kept.Id).IsValid().Should().BeTrue();
    }

    [Fact]
    public async Task ByTheTimeTheFolderIsGoneItsToolsAreAlreadyRemoved()
    {
        var loadout = await CreateLoadout();
        var paths = ServiceProvider.GetRequiredService<WabbajackPaths>();
        var removed = await AddGroup(loadout, "Removed", withExtras: true);
        var folder = paths.ExtrasFor("Removed", removed.Id);
        await folder.Combine("tool.exe").WriteAllTextAsync("x");
        await AddTool(loadout, "Removed", folder.Combine("tool.exe").ToString());

        // Revisions reach their subscribers in order, the watcher first, so a folder deleted before the cleanup commits is
        // seen here while the latest db still has its tool.
        var dangling = new List<TxId>();
        using var probe = Connection.Revisions.Subscribe(_ =>
        {
            if (folder.DirectoryExists()) return;
            var db = Connection.Db;
            if (!UserTool.FindByInstallation(db, loadout.InstallationId).Any(t => t.Name == "Removed")) return;
            lock (dangling) dangling.Add(db.BasisTxId);
        });

        await ServiceProvider.GetRequiredService<ILoadoutManager>().RemoveCollection(CollectionGroupId.From(removed.Id));

        await WaitUntil(() => !folder.DirectoryExists());
        ToolNames(loadout).Should().NotContain("Removed");
        lock (dangling) dangling.Should().BeEmpty("the folder is only deleted once the tool rows are gone");
    }

    [Fact]
    public async Task RemovingOneOfTwoListsWithTheSameNameLeavesTheOthersFolderAndTools()
    {
        var loadout = await CreateLoadout();
        var paths = ServiceProvider.GetRequiredService<WabbajackPaths>();
        var first = await AddGroup(loadout, "Same", withExtras: true);
        var second = await AddGroup(loadout, "Same", withExtras: true);
        foreach (var (name, group) in new[] { ("First", first), ("Second", second) })
        {
            var folder = paths.ExtrasFor("Same", group.Id);
            await folder.Combine("tool.exe").WriteAllTextAsync("x");
            await AddTool(loadout, name, folder.Combine("tool.exe").ToString());
        }

        await ServiceProvider.GetRequiredService<ILoadoutManager>().RemoveCollection(CollectionGroupId.From(first.Id));

        await WaitUntil(() => !paths.ExtrasFor("Same", first.Id).Combine("tool.exe").FileExists);
        await WaitUntil(() => ToolNames(loadout).All(n => n != "First"));
        paths.ExtrasFor("Same", second.Id).Combine("tool.exe").FileExists.Should().BeTrue();
        ToolNames(loadout).Should().Contain("Second");
    }

    [Fact]
    public async Task AnExtrasFolderOutsideTheExtrasRootIsNeverDeleted()
    {
        var loadout = await CreateLoadout();
        await using var planted = TemporaryFileManager.CreateFolder();
        var file = planted.Path.Combine("precious.txt");
        await file.WriteAllTextAsync("keep");
        var paths = ServiceProvider.GetRequiredService<WabbajackPaths>();
        var sibling = await AddGroup(loadout, "Sibling", withExtras: true);
        var siblingFile = paths.ExtrasFor("Sibling", sibling.Id).Combine("marker.txt");
        await siblingFile.WriteAllTextAsync("x");
        var group = await AddGroup(loadout, "Planted", planted.Path.ToString());
        await AddTool(loadout, "Inside", planted.Path.Combine("tool.exe").ToString());

        await ServiceProvider.GetRequiredService<ILoadoutManager>().RemoveCollection(CollectionGroupId.From(group.Id));
        // Cleanups run one at a time in removal order, so once the sibling's folder is gone the planted group's cleanup has run too.
        await ServiceProvider.GetRequiredService<ILoadoutManager>().RemoveCollection(CollectionGroupId.From(sibling.Id));
        await WaitUntil(() => !siblingFile.FileExists);

        file.FileExists.Should().BeTrue();
        ToolNames(loadout).Should().Contain("Inside", "a tool outside the extras root is not the list's");
    }

    private IEnumerable<string> ToolNames(Loadout.ReadOnly loadout)
        => UserTool.FindByInstallation(Connection.Db, loadout.InstallationId).Select(t => t.Name);

    private async Task AddTool(Loadout.ReadOnly loadout, string name, string exe)
    {
        using var tx = Connection.BeginTransaction();
        _ = new UserTool.New(tx) { InstallationId = loadout.InstallationId, Name = name, Exe = exe, Arguments = "", RunVia = ToolRunVia.Auto };
        await tx.Commit();
    }

    /// <summary>Adds a group whose folder is the one <see cref="WabbajackPaths.ExtrasFor"/> names for it, which needs the group's id and so is recorded after it exists.</summary>
    private async Task<WabbajackModlistLoadoutGroup.ReadOnly> AddGroup(Loadout.ReadOnly loadout, string name, bool withExtras)
    {
        var group = await AddGroup(loadout, name, (string?)null);
        if (!withExtras) return group;
        var folder = ServiceProvider.GetRequiredService<WabbajackPaths>().ExtrasFor(name, group.Id);
        using var tx = Connection.BeginTransaction();
        tx.Add(group.Id, WabbajackModlistLoadoutGroup.ExtrasFolder, folder.ToString());
        await tx.Commit();
        return WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id);
    }

    private async Task<WabbajackModlistLoadoutGroup.ReadOnly> AddGroup(Loadout.ReadOnly loadout, string name, string? extrasFolder)
    {
        var modlist = await AddModlist(new ListBuilder().ToList(name), new Dictionary<string, byte[]>());
        using var tx = Connection.BeginTransaction();
        var group = new WabbajackModlistLoadoutGroup.New(tx, out var id)
        {
            ModlistId = modlist.Id,
            ExtrasFolder = extrasFolder,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout.LoadoutId, IsDisabled = true },
                },
            },
        };
        var result = await tx.Commit();
        return result.Remap(group);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(25);
        }
    }
}

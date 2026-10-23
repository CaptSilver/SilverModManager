using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Wabbajack.Install;
using NexusMods.Wabbajack.Models;
using NexusMods.Wabbajack.Tests.Install;

namespace NexusMods.Wabbajack.Tests.Diagnostics;

public class ExtrasCleanupTests(ITestOutputHelper helper) : InstallTestBase<ExtrasCleanupTests>(helper)
{
    [Fact]
    public async Task RemovingTheCollectionDeletesItsExtrasFolderAndToolsAndLeavesASiblingsAlone()
    {
        var loadout = await CreateLoadout();
        var paths = ServiceProvider.GetRequiredService<WabbajackPaths>();
        var removed = await AddGroup(loadout, "Removed", paths.ExtrasFor("Removed").ToString());
        var kept = await AddGroup(loadout, "Kept", paths.ExtrasFor("Kept").ToString());
        foreach (var name in new[] { "Removed", "Kept" })
        {
            var folder = paths.ExtrasFor(name);
            folder.Combine("tool").CreateDirectory();
            await folder.Combine("tool/tool.exe").WriteAllTextAsync("x");
            await AddTool(loadout, name, folder.Combine("tool/tool.exe").ToString());
        }
        await AddTool(loadout, "Unrelated", "/somewhere/else/tool.exe");

        await ServiceProvider.GetRequiredService<ILoadoutManager>().RemoveCollection(CollectionGroupId.From(removed.Id));

        await WaitUntil(() => !paths.ExtrasFor("Removed").Combine("tool/tool.exe").FileExists);
        await WaitUntil(() => ToolNames(loadout).All(n => n != "Removed"));
        paths.ExtrasFor("Kept").Combine("tool/tool.exe").FileExists.Should().BeTrue();
        ToolNames(loadout).Should().BeEquivalentTo(["Kept", "Unrelated"]);
        WabbajackModlistLoadoutGroup.Load(Connection.Db, kept.Id).IsValid().Should().BeTrue();
    }

    [Fact]
    public async Task AnExtrasFolderOutsideTheExtrasRootIsNeverDeleted()
    {
        var loadout = await CreateLoadout();
        await using var planted = TemporaryFileManager.CreateFolder();
        var file = planted.Path.Combine("precious.txt");
        await file.WriteAllTextAsync("keep");
        var paths = ServiceProvider.GetRequiredService<WabbajackPaths>();
        var siblingFile = paths.ExtrasFor("Sibling").Combine("marker.txt");
        await siblingFile.WriteAllTextAsync("x");
        var sibling = await AddGroup(loadout, "Sibling", paths.ExtrasFor("Sibling").ToString());
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

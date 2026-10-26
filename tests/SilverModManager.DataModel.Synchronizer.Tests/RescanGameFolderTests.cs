using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// External Changes follow the game folder when something other than a sync changes it, for example a cleanup
/// that removes another mod manager's files.
/// </summary>
public class RescanGameFolderTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<RescanGameFolderTests>(helper)
{
    private static IEnumerable<GamePath> ExternalChangePaths(Loadout.ReadOnly loadout) =>
        LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Where(f => f.AsLoadoutItem().Parent.AsLoadoutItem().Name == "Overrides")
            .Select(f => (GamePath)f.TargetPath);

    [Fact]
    public async Task AnExternalChangeDeletedFromDiskLeavesTheListWithoutASync()
    {
        var loadout = await CreateLoadout();
        var gameFolder = loadout.InstallationInstance.Locations[LocationId.Game].Path;
        var kept = gameFolder / "kept.json";
        var removed = gameFolder / "removed.json";
        await kept.WriteAllTextAsync("kept");
        await removed.WriteAllTextAsync("removed");
        loadout = await Synchronizer.Synchronize(loadout);
        ExternalChangePaths(loadout).Should().Contain([new GamePath(LocationId.Game, "kept.json"), new GamePath(LocationId.Game, "removed.json")]);

        removed.Delete();
        await LoadoutManager.RescanGameFolder(Refresh(loadout));

        var after = ExternalChangePaths(Refresh(loadout)).ToArray();
        after.Should().Contain(new GamePath(LocationId.Game, "kept.json"));
        after.Should().NotContain(new GamePath(LocationId.Game, "removed.json"));
        kept.FileExists.Should().BeTrue("a rescan only reads the folder");

        await Synchronizer.Synchronize(Refresh(loadout));
        removed.FileExists.Should().BeFalse("the next sync must agree that the file is gone, not deploy it again");
        kept.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AnExternalChangeThatWasNeverOnDiskStaysInTheList()
    {
        var loadout = await CreateLoadout();
        var pending = new GamePath(LocationId.Game, "waiting/for/deploy.json");
        using (var tx = Connection.BeginTransaction())
        {
            var overrides = ((ALoadoutSynchronizer)Synchronizer).GetOrCreateOverridesGroup(tx, loadout);
            OverridesGroupItems.UpsertFile(tx, loadout, overrides, pending, "pending".xxHash3AsUtf8(), Size.From(7));
            await tx.Commit();
        }

        await LoadoutManager.RescanGameFolder(Refresh(loadout));

        ExternalChangePaths(Refresh(loadout)).Should().Contain(pending);
    }
}

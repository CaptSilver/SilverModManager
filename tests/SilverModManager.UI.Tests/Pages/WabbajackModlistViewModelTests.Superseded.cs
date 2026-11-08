using FluentAssertions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.UI.Tests.Pages;

public partial class WabbajackModlistViewModelTests
{
    [Fact]
    public async Task ThePageOfAFileAnUpdateSwitchedAwayFromNamesTheVersionThatReplacedIt()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Superseded page");
        var one = await WabbajackGroupSeed.AddList(Connection, "1.0", "page-list");
        var two = await WabbajackGroupSeed.AddList(Connection, "2.0", "page-list");
        var group = await WabbajackGroupSeed.AddGroup(Connection, one.Id, loadout.LoadoutId, one.Name);
        await WabbajackGroupSeed.Finish(Connection, group);
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        vm.FileId = LibraryFileId.From(one.Id);
        vm.PinnedLoadout = loadout.LoadoutId;
        await vm.LoadAsync(CancellationToken.None);
        using var activation = vm.Activator.Activate();
        vm.SupersededBy.Should().BeNull("the list is installed, not replaced");

        await WabbajackGroupSeed.Switch(Connection, group, one.Id, two.Id);

        await EventuallyOnUi(() =>
        {
            vm.SupersededBy.Should().Be("2.0");
            vm.InstallBlockedReason.Should().Contain("2.0");
        });
    }

    [Fact]
    public async Task ThePageOfANewerFileNamesTheVersionOfTheListInstalledHereAndBlocksInstall()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Other version page");
        var one = await WabbajackGroupSeed.AddList(Connection, "3.2", "other-page-list");
        var two = await WabbajackGroupSeed.AddList(Connection, "3.3", "other-page-list");
        var group = await WabbajackGroupSeed.AddGroup(Connection, one.Id, loadout.LoadoutId, one.Name);
        await WabbajackGroupSeed.Finish(Connection, group);
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        vm.FileId = LibraryFileId.From(two.Id);
        vm.PinnedLoadout = loadout.LoadoutId;
        await vm.LoadAsync(CancellationToken.None);
        using var activation = vm.Activator.Activate();

        await EventuallyOnUi(() =>
        {
            vm.OtherVersion.Should().Be("3.2");
            vm.SupersededBy.Should().BeNull();
            vm.InstallBlockedReason.Should().Contain("3.2").And.Contain("Update to");
        });
    }
}

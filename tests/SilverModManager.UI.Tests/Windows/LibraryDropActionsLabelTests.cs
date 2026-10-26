using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Abstractions.Library;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Windows;

public class LibraryDropActionsLabelTests(IServiceProvider provider) : AUiTest(provider)
{
    private string? CurrentLoadoutNameWhenActive(Loadout.ReadOnly loadout)
    {
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Context.Returns(new LoadoutContext { LoadoutId = loadout.LoadoutId });
        var controller = Substitute.For<IWorkspaceController>();
        controller.ActiveWorkspace.Returns(workspace);
        return new LibraryDropActions(Substitute.For<ILibraryService>(), Connection, controller).CurrentLoadoutName;
    }

    [Fact]
    public async Task OneLoadoutOfTheGame_IsNamedAfterTheGameOnly()
    {
        var only = await SeedLoadoutRow(GameStore.Steam, "Only one");

        CurrentLoadoutNameWhenActive(only).Should().Be(only.InstallationInstance.Game.DisplayName);
    }

    [Fact]
    public async Task SeveralLoadoutsOfTheGame_AddTheLoadoutName()
    {
        var first = await SeedLoadoutRow(GameStore.Steam, "First");
        await SeedSiblingLoadoutRow(first, "Second");

        CurrentLoadoutNameWhenActive(first).Should().Be($"{first.InstallationInstance.Game.DisplayName} - First");
    }
}

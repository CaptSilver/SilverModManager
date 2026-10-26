using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Tests.Helpers;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

public class LoadoutApplierRefusalTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class Fixture
    {
        public required IServiceProvider Services { get; init; }
        public required IWindowManager Windows { get; init; }
        public required IWorkspaceController Workspaces { get; init; }
        public required IWindowNotificationService Notifications { get; init; }
        public required Loadout.ReadOnly Loadout { get; init; }
    }

    // What the controller resolves for a default (left-menu) navigation; the dialog must open the page with it.
    private static readonly OpenPageBehavior LeftMenuBehavior = new OpenPageBehavior.ReplaceTab(Optional<PanelId>.None, Optional<PanelTabId>.None);

    private async Task<Fixture> Refusing(ButtonDefinitionId answer, Exception? failure = null)
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "Refused " + Guid.NewGuid());
        var synchronizer = Substitute.For<ISynchronizerService>();
        synchronizer.Synchronize(Arg.Any<LoadoutId>())
            .Returns<Task>(_ => throw (failure ?? Refusal(loadout.LoadoutId)));

        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Id.Returns(WorkspaceId.From(Guid.NewGuid()));
        workspace.SelectedPanel.Returns(Substitute.For<IPanelViewModel>());
        var workspaces = Substitute.For<IWorkspaceController>();
        workspaces.GetOpenPageBehavior(Arg.Any<PageData>(), Arg.Any<NavigationInformation>()).Returns(LeftMenuBehavior);
        workspaces.ChangeOrCreateWorkspaceByContext(Arg.Any<Func<LoadoutContext, bool>>(), Arg.Any<Func<Optional<PageData>>>(), Arg.Any<Func<LoadoutContext>>())
            .Returns(workspace);

        var rig = new PresenterRig(Provider);
        rig.Windows.ActiveWorkspaceController.Returns(workspaces);
        rig.Answer = _ => Task.FromResult(new StandardDialogResult { ButtonId = answer });

        var notifications = Substitute.For<IWindowNotificationService>();
        var services = new OverridingServiceProvider(rig.Services, (typeof(ISynchronizerService), synchronizer), (typeof(IWindowNotificationService), notifications));
        return new Fixture { Services = services, Windows = rig.Windows, Workspaces = workspaces, Notifications = notifications, Loadout = loadout };
    }

    [Fact]
    public async Task ARefusedApply_ShowsTheReasonInADialogAndReturnsFalse()
    {
        var f = await Refusing(ButtonDefinitionId.Accept);

        var applied = await OnUi(() => LoadoutApplier.Apply(f.Services, f.Loadout.LoadoutId));

        applied.Should().BeFalse();
        await f.Windows.Received(1).ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal);
        f.Notifications.DidNotReceiveWithAnyArgs().ShowToast(default!);
    }

    // The synchronizer service attaches the way to Health Check before the refusal reaches the caller.
    private static SynchronizerPreflightException Refusal(LoadoutId loadoutId) =>
        new("Can't merge regulation.bin: no vanilla to compare against.") { Action = new OpenHealthCheck(loadoutId) };

    [Fact]
    public async Task OkOnTheRefusal_LeavesTheWorkspaceAlone()
    {
        var f = await Refusing(ButtonDefinitionId.Accept);

        await OnUi(() => LoadoutApplier.Apply(f.Services, f.Loadout.LoadoutId));

        f.Workspaces.DidNotReceiveWithAnyArgs().OpenPage(default, default, default!);
    }

    [Fact]
    public async Task OpenHealthCheckOnTheRefusal_OpensTheLoadoutsDiagnosticList()
    {
        var f = await Refusing(ErrorPresenter.OpenHealthCheckButton);

        var applied = await OnUi(() => LoadoutApplier.Apply(f.Services, f.Loadout.LoadoutId));

        applied.Should().BeFalse();
        f.Workspaces.Received(1).OpenPage(
            Arg.Any<WorkspaceId>(),
            Arg.Is<Optional<PageData>>(page => page.HasValue
                && page.Value.FactoryId == DiagnosticListPageFactory.StaticId
                && ((DiagnosticListPageContext)page.Value.Context).LoadoutId == f.Loadout.LoadoutId),
            LeftMenuBehavior, Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task AFailureThatIsNotARefusal_IsReportedAsACrashWithoutTheMessage()
    {
        var f = await Refusing(ButtonDefinitionId.Accept, new InvalidOperationException("disk on fire"));

        var applied = await OnUi(() => LoadoutApplier.Apply(f.Services, f.Loadout.LoadoutId));

        applied.Should().BeFalse();
        await f.Windows.Received(1).ShowDialog(Arg.Is<IDialog>(d => !PresenterRig.Body(((Dialog)d).ViewModel).Contains("disk on fire")), DialogWindowType.Modal);
        f.Notifications.DidNotReceiveWithAnyArgs().ShowToast(default!);
    }
}

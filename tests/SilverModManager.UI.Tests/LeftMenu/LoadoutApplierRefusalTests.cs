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
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
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
            .Returns<Task>(_ => throw (failure ?? new SynchronizerPreflightException("Can't merge regulation.bin: no vanilla to compare against.")));

        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Id.Returns(WorkspaceId.From(Guid.NewGuid()));
        workspace.SelectedPanel.Returns(Substitute.For<IPanelViewModel>());
        var workspaces = Substitute.For<IWorkspaceController>();
        workspaces.GetOpenPageBehavior(Arg.Any<PageData>(), Arg.Any<NavigationInformation>()).Returns(LeftMenuBehavior);
        workspaces.ChangeOrCreateWorkspaceByContext(Arg.Any<Func<LoadoutContext, bool>>(), Arg.Any<Func<Optional<PageData>>>(), Arg.Any<Func<LoadoutContext>>())
            .Returns(workspace);

        var windows = Substitute.For<IWindowManager>();
        windows.ActiveWorkspaceController.Returns(workspaces);
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>())
            .Returns(Task.FromResult(new StandardDialogResult { ButtonId = answer }));

        var notifications = Substitute.For<IWindowNotificationService>();
        var services = new ProviderWith(Provider, (typeof(ISynchronizerService), synchronizer), (typeof(IWindowManager), windows), (typeof(IWindowNotificationService), notifications));
        return new Fixture { Services = services, Windows = windows, Workspaces = workspaces, Notifications = notifications, Loadout = loadout };
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
        var f = await Refusing(LoadoutApplierRefusal.OpenHealthCheck);

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
    public async Task AFailureThatIsNotARefusal_StillPropagates()
    {
        var f = await Refusing(ButtonDefinitionId.Accept, new InvalidOperationException("disk on fire"));

        var act = () => OnUi(() => LoadoutApplier.Apply(f.Services, f.Loadout.LoadoutId));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("disk on fire");
        await f.Windows.DidNotReceiveWithAnyArgs().ShowDialog(default!, default);
    }

    private sealed class ProviderWith(IServiceProvider inner, params (Type Service, object Instance)[] overrides) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            overrides.Where(o => o.Service == serviceType).Select(o => o.Instance).FirstOrDefault() ?? inner.GetService(serviceType);
    }
}

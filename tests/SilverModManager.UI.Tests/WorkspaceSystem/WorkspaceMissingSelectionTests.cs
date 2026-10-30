using Avalonia.Controls;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Reactive.Linq;
using ReactiveUI;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Controls.TopBar;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.UI.Tests.WorkspaceSystem;

/// <summary>
/// A workspace that has no panel yet, and the dialog pieces that can lose their view model or window,
/// say what is missing or do nothing instead of dereferencing null.
/// </summary>
public class WorkspaceMissingSelectionTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task NewWorkspace_HasNoSelectedPanelOrTab()
    {
        await OnUi(() =>
        {
            var workspace = new WorkspaceViewModel(NullLoggerFactory.Instance, Substitute.For<IWorkspaceController>(), new PageFactoryController([]));

            workspace.SelectedPanel.Should().BeNull();
            workspace.SelectedTab.Should().BeNull();
        });
    }

    [Theory]
    [InlineData(OpenPageBehaviorType.ReplaceTab)]
    [InlineData(OpenPageBehaviorType.NewTab)]
    public void OpenBehavior_WithoutASelectedPanel_NamesTheMissingPanel(OpenPageBehaviorType type)
    {
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.SelectedPanel.Returns((IPanelViewModel?)null);

        var act = () => WorkspaceController.CreateOpenPageBehavior(workspace, type);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no selected panel*");
    }

    [Fact]
    public void OpenBehavior_WithoutASelectedTab_NamesTheMissingTab()
    {
        var panel = Substitute.For<IPanelViewModel>();
        panel.SelectedTab.Returns((IPanelTabViewModel?)null);
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.SelectedPanel.Returns(panel);

        var act = () => WorkspaceController.CreateOpenPageBehavior(workspace, OpenPageBehaviorType.ReplaceTab);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no selected tab*");
    }

    [Fact]
    public void OpenBehavior_NewTab_NeedsNoSelectedTab()
    {
        var panel = Substitute.For<IPanelViewModel>();
        panel.SelectedTab.Returns((IPanelTabViewModel?)null);
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.SelectedPanel.Returns(panel);

        WorkspaceController.CreateOpenPageBehavior(workspace, OpenPageBehaviorType.NewTab).IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task DialogWindow_LosingItsViewModel_DoesNotThrow()
    {
        await OnUi(() =>
        {
            var viewModel = Substitute.For<IDialogViewModel>();
            viewModel.ButtonPressCommand.Returns(new R3.ReactiveCommand<ButtonDefinitionId, ButtonDefinitionId>(id => id));
            var window = new DialogWindow { DataContext = viewModel };

            var act = () => window.DataContext = null;

            act.Should().NotThrow();
        });
    }

    [Fact]
    public async Task CopyDetails_WithoutAWindow_DoesNothing()
    {
        await OnUi(async () =>
        {
            var view = new DialogStandardContentView { ViewModel = new DialogStandardContentDesignViewModel() };
            var window = Track(new Window { Content = view });
            ShowRendered(window);
            var command = (ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit>)view.CopyDetailsButton.Command!;
            window.Content = null;

            var act = async () => await command.Execute().FirstOrDefaultAsync();

            await act.Should().NotThrowAsync();
        });
    }

    [Fact]
    public async Task NewTabCommand_WithoutASelectedPanel_NamesTheMissingPanel()
    {
        await OnUi(async () =>
        {
            var workspace = Substitute.For<IWorkspaceViewModel>();
            workspace.SelectedPanel.Returns((IPanelViewModel?)null);
            var controller = Substitute.For<IWorkspaceController>();
            controller.ActiveWorkspace.Returns(workspace);
            var windowManager = Substitute.For<IWindowManager>();
            windowManager.ActiveWorkspaceController.Returns(controller);
            var topBar = new TopBarViewModel(Substitute.For<IServiceProvider>(), NullLogger<TopBarViewModel>.Instance,
                Substitute.For<ILoginManager>(), windowManager, Substitute.For<IOverlayController>(),
                Substitute.For<IOSInterop>(), Substitute.For<ISettingsManager>(), Substitute.For<IFileSystem>(),
                Substitute.For<IWindowNotificationService>(), Substitute.For<INexusCredentialSummary>());

            var act = async () => await topBar.NewTabCommand.Execute().FirstOrDefaultAsync();

            // ReactiveCommand wraps a throwing execute body in UnhandledErrorException.
            (await act.Should().ThrowAsync<ReactiveUI.UnhandledErrorException>())
                .WithInnerException<InvalidOperationException>().WithMessage("*no selected panel*");
        });
    }
}

using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ReactiveUI;
using SilverModManager.App.UI.Controls.Alerts;
using SilverModManager.App.UI.Controls.LoadoutBadge;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Controls.Settings.SettingEntries.PathsList;
using SilverModManager.App.UI.Controls.Spine;
using SilverModManager.App.UI.Controls.Spine.Buttons.Download;
using SilverModManager.App.UI.Controls.Spine.Buttons.Icon;
using SilverModManager.App.UI.Controls.TopBar;
using SilverModManager.App.UI.WorkspaceAttachments;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.UI.Tests.Controls;

/// <summary>
/// Controls whose view model, window, storage provider or settings can be missing when an event reaches them:
/// they do nothing, or refuse in words the player can read, instead of dereferencing null.
/// </summary>
public class ControlsMissingContextTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class CapturingSink : ILogSink
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) => true;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Messages.Add(messageTemplate);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) => Messages.Add(messageTemplate);
    }

    [Theory]
    [InlineData("MinimizeButton")]
    [InlineData("MaximizeButton")]
    [InlineData("CloseButton")]
    public async Task TopBarWindowButtons_WithoutAWindow_DoNothingAndTrace(string buttonName)
    {
        await OnUi(() =>
        {
            var sink = new CapturingSink();
            var previous = Logger.Sink;
            Logger.Sink = sink;
            try
            {
                var view = new TopBarView();
                var button = view.FindControl<Button>(buttonName)!;

                var act = () => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                act.Should().NotThrow();
                sink.Messages.Should().ContainSingle(m => m.Contains("window"));
            }
            finally
            {
                Logger.Sink = previous;
            }
        });
    }

    [Fact]
    public async Task TopBarHistoryButtons_FollowTheSelectedTab()
    {
        await OnUi(() =>
        {
            var tab = Substitute.For<IPanelTabViewModel>();
            tab.GoBackInHistoryCommand.Returns(ReactiveCommand.Create(() => { }));
            tab.GoForwardInHistoryCommand.Returns(ReactiveCommand.Create(() => { }));

            var withTab = new TopBarView { ViewModel = new TopBarDesignViewModel { SelectedTab = tab } };
            var withoutTab = new TopBarView { ViewModel = new TopBarDesignViewModel() };
            var window = Track(new Window { Content = new StackPanel { Children = { withTab, withoutTab } } });
            ShowRendered(window);

            withTab.FindControl<Button>("GoBackInHistory")!.Command.Should().BeSameAs(tab.GoBackInHistoryCommand);
            withTab.FindControl<Button>("GoForwardInHistory")!.Command.Should().BeSameAs(tab.GoForwardInHistoryCommand);
            withoutTab.FindControl<Button>("GoBackInHistory")!.Command.Should().BeNull();
        });
    }

    [Fact]
    public async Task DismissingAnAlert_WithoutSettings_ReturnsWithoutDismissing()
    {
        var alert = await OnUi(() => Task.FromResult(new Alert()));

        var result = await OnUi(async () => await alert.DismissCommand.Execute());

        result.Should().BeNull();
        (await OnUi(() => Task.FromResult(alert.IsDismissed))).Should().BeFalse();
    }

    [Fact]
    public async Task ChangingAPath_WithoutAStorageProvider_RefusesInWordsThePlayerCanRead()
    {
        var container = new ConfigurablePathsContainer([], [], new PropertyConfig(
            "Paths",
            typeof(ConfigurablePath[]),
            new PropertyOptionsWrapper { DescriptionFactory = _ => "", DisplayName = "Paths", Section = Sections.General },
            ContainerOptions: null,
            _ => Array.Empty<ConfigurablePath>(),
            _ => Array.Empty<ConfigurablePath>(),
            (_, _) => { }));
        var viewModel = new SettingPathsViewModel(Substitute.For<IOSInterop>(), container);

        var act = async () => await viewModel.ChangeLocation();

        (await act.Should().ThrowAsync<UserFacingException>())
            .WithMessage("This window cannot open a folder picker*");
    }

    [Fact]
    public async Task MarkdownRenderer_WithoutAViewModel_ShowsNothing()
    {
        await OnUi(() =>
        {
            var view = new MarkdownRendererView();
            var window = Track(new Window { Content = view });

            var act = () => ShowRendered(window);

            act.Should().NotThrow();
        });
    }

    [Fact]
    public async Task LoadoutBadge_ShowsAViewModelGivenAfterActivation()
    {
        await OnUi(() =>
        {
            var view = new LoadoutBadge();
            var window = Track(new Window { Content = view });
            ShowRendered(window);
            view.Classes.Should().NotContain(":selected");

            var viewModel = new LoadoutBadgeDesignViewModel { IsLoadoutSelected = true };
            view.ViewModel = viewModel;
            Render(window);

            view.Classes.Contains(":selected").Should().BeTrue();
            viewModel.IsLoadoutSelected = false;
            Render(window);
            view.Classes.Contains(":selected").Should().BeFalse();
        });
    }

    [Fact]
    public async Task AnIconThatCannotBeDecoded_GetsAPlaceholderImage()
    {
        var windows = Substitute.For<IWindowManager>();
        var controller = Substitute.For<IWorkspaceController>();
        controller.AllWorkspaces.Returns(new System.Collections.ObjectModel.ReadOnlyObservableCollection<IWorkspaceViewModel>([]));
        windows.ActiveWorkspaceController.Returns(controller);

        var image = await OnUi(() => Task.FromResult(new SpineViewModel(
            Provider,
            Provider.GetRequiredService<ILogger<SpineViewModel>>(),
            Connection,
            windows,
            new IconButtonViewModel(),
            new IconButtonViewModel(),
            Substitute.For<ISpineDownloadButtonViewModel>(),
            Substitute.For<IWorkspaceAttachmentsFactoryManager>()).LoadImageFromStream(new MemoryStream([1, 2, 3]))));

        image.Should().NotBeNull();
    }
}

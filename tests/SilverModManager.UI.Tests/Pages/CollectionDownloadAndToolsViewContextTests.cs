using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentAssertions;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages.Tools;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The collection download and tools pages when the view model they observe is missing or is a design stand-in.
/// </summary>
public class CollectionDownloadAndToolsViewContextTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task CollectionDownloadDesignViewModel_HasADownloadsTreeToBind()
    {
        await OnUi(() =>
        {
            var viewModel = new CollectionDownloadDesignViewModel();

            viewModel.TreeDataGridAdapter.Should().NotBeNull();
            viewModel.TreeDataGridAdapter.Filter.Value.Should().Be(CollectionDownloadsFilter.OnlyRequired);
        });
    }

    [Fact]
    public async Task SavingAUserTool_WithoutAViewModel_LeavesTheFormAlone()
    {
        await OnUi(() =>
        {
            var view = new ToolsPageView();
            var window = Track(new Window { Content = view });
            ShowRendered(window);
            view.FindControl<TextBox>("UserToolNameBox")!.Text = "My tool";

            var act = () => view.FindControl<Button>("SaveUserToolButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            act.Should().NotThrow();
            view.FindControl<TextBox>("UserToolNameBox")!.Text.Should().Be("My tool", "nothing was saved, so there is nothing to reset");
        });
    }
}

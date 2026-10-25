using Avalonia.Controls;
using Avalonia.Threading;
using DynamicData;
using SilverModManager.App.UI.Controls;
using SilverModManager.MnemonicDB.Abstractions;
using Observable = System.Reactive.Linq.Observable;
using FluentAssertions;
using SilverModManager.App.UI.Pages.CollectionDownload;

namespace SilverModManager.UI.Tests;

public class CollectionDownloadEmbeddedTests(IServiceProvider provider) : AUiTest(provider)
{
    /// <summary>
    /// A downloads tree with no rows, so the view can be shown without a collection in the database.
    /// </summary>
    private sealed class NoRowsAdapter(IServiceProvider provider) : CollectionDownloadTreeDataGridAdapter(provider, default, default)
    {
        protected override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> GetRootsObservable(bool viewHierarchical) =>
            Observable.Empty<IChangeSet<CompositeItemModel<EntityId>, EntityId>>();
    }

    private async Task<CollectionDownloadView> ShowDownloadView(bool isEmbedded)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // The design view model has collection instructions, so the tab would show if nothing hid it.
            var view = new CollectionDownloadView { ViewModel = new CollectionDownloadDesignViewModel(isEmbedded, new NoRowsAdapter(Provider)) };
            new Window { Width = 1000, Height = 800, Content = view }.Show();
            return view;
        });
    }

    private static T Find<T>(Control view, string name) where T : Control =>
        view.FindControl<T>(name) ?? throw new InvalidOperationException($"No control `{name}`");

    [Fact]
    public async Task StandaloneDownloadViewShowsItsInstructionsTab()
    {
        var view = await ShowDownloadView(isEmbedded: false);

        await Dispatcher.UIThread.InvokeAsync(() => Find<TabItem>(view, "InstructionsTab").IsVisible.Should().BeTrue());
    }

    [Fact]
    public async Task EmbeddedDownloadViewHidesItsInstructionsTab()
    {
        var view = await ShowDownloadView(isEmbedded: true);

        await Dispatcher.UIThread.InvokeAsync(() => Find<TabItem>(view, "InstructionsTab").IsVisible.Should().BeFalse("the collection page has its own Instructions tab"));
    }

    [Fact]
    public async Task EmbeddedDownloadViewSelectsAVisibleTab()
    {
        var view = await ShowDownloadView(isEmbedded: true);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var selected = Find<TabControl>(view, "TabControl").SelectedItem.Should().BeOfType<TabItem>().Subject;
            selected.IsVisible.Should().BeTrue();
        });
    }

    [Theory]
    [InlineData(false, "Additional Instructions")]
    [InlineData(true, "Instructions")]
    public async Task IncorrectFileHintNamesTheTabThatHoldsTheInstructions(bool isEmbedded, string tabName)
    {
        var view = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new ManualDownloadRequiredOverlayView { ViewModel = new ManualDownloadRequiredOverlayDesignViewModel(isEmbedded, isIncorrectFile: true) };
            new Window { Width = 800, Height = 600, Content = view }.Show();
            return view;
        });

        var text = await Dispatcher.UIThread.InvokeAsync(() => Find<TextBlock>(view, "ErrorGuidanceTextBlock").Text);

        text.Should().Contain($"'{tabName}' tab");
    }
}

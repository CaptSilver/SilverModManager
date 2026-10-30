using Avalonia.Controls;
using Avalonia.Threading;
using DynamicData;
using SilverModManager.App.UI.Controls;
using SilverModManager.MnemonicDB.Abstractions;
using Observable = System.Reactive.Linq.Observable;
using FluentAssertions;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Pages;

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
            Track(new Window { Width = 1000, Height = 800, Content = view }).Show();
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

    [Fact]
    public async Task DownloadButtonsFollowTheFilterOfTheCurrentViewModel()
    {
        var first = new NoRowsAdapter(Provider);
        var second = new NoRowsAdapter(Provider);
        var view = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new CollectionDownloadView { ViewModel = new CollectionDownloadDesignViewModel(isEmbedded: false, first) };
            Track(new Window { Width = 1000, Height = 800, Content = view }).Show();
            return view;
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            view.ViewModel = new CollectionDownloadDesignViewModel(isEmbedded: false, second);
            second.Filter.Value = CollectionDownloadsFilter.OnlyOptional;
            Find<Control>(view, "ButtonDownloadOptionalItems").IsVisible.Should().BeTrue("the second view model's filter is the one that counts");

            first.Filter.Value = CollectionDownloadsFilter.OnlyRequired;
            second.Filter.Value = CollectionDownloadsFilter.OnlyRequired;
            Find<Control>(view, "ButtonDownloadOptionalItems").IsVisible.Should().BeFalse();

            second.Filter.Value = CollectionDownloadsFilter.OnlyOptional;
            first.Filter.Value = CollectionDownloadsFilter.OnlyRequired;
            Find<Control>(view, "ButtonDownloadOptionalItems").IsVisible.Should().BeTrue("the replaced view model's filter no longer matters");
        });
    }

    [Fact]
    public async Task SpinnerStopsFollowingDownloadStateOnceTheViewIsDetached()
    {
        var viewModel = new CollectionDownloadDesignViewModel(isEmbedded: false, new NoRowsAdapter(Provider));
        var (window, view) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new CollectionDownloadView { ViewModel = viewModel };
            var window = Track(new Window { Width = 1000, Height = 800, Content = view });
            window.Show();
            return (window, view);
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            viewModel.IsDownloading.Value = true;
            Find<Control>(view, "Spinner").IsVisible.Should().BeTrue();

            window.Content = null;
            viewModel.IsDownloading.Value = false;
            Find<Control>(view, "Spinner").IsVisible.Should().BeTrue("a detached view no longer follows the view model");
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
            Track(new Window { Width = 800, Height = 600, Content = view }).Show();
            return view;
        });

        var text = await Dispatcher.UIThread.InvokeAsync(() => Find<TextBlock>(view, "ErrorGuidanceTextBlock").Text);

        text.Should().Contain($"'{tabName}' tab");
    }
}

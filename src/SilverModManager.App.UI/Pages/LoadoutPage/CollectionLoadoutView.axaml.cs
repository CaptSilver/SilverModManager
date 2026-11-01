using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.ReactiveUI;
using Humanizer;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using R3;
using ReactiveUI;
using SilverModManager.UI.Sdk;
using CompositeDisposable = System.Reactive.Disposables.CompositeDisposable;
using Disposable = System.Reactive.Disposables.Disposable;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

public partial class CollectionLoadoutView : ReactiveUserControl<ICollectionLoadoutViewModel>
{
    public CollectionLoadoutView()
    {
        InitializeComponent();

        TreeDataGridViewHelper.SetupTreeDataGridAdapter<CollectionLoadoutView, ICollectionLoadoutViewModel, CompositeItemModel<EntityId>, EntityId>(this, TreeDataGrid, vm => vm.Adapter);

        this.WhenActivated(disposables =>
        {
            // The adapter is null while there is no installed group, so the tree follows it, and then its source, from null.
            this.WhenAnyValue(view => view.ViewModel)
                .Select(vm => vm is null
                    ? System.Reactive.Linq.Observable.Return<LoadoutTreeDataGridAdapter?>(null)
                    : vm.WhenAnyValue(model => model.Adapter))
                .Switch()
                .Select(adapter => adapter is null
                    ? System.Reactive.Linq.Observable.Return<ITreeDataGridSource?>(null)
                    : adapter.WhenAnyValue(a => a.Source.Value).Select(source => (ITreeDataGridSource?)source))
                .Switch()
                .Subscribe(source => TreeDataGrid.Source = source)
                .AddTo(disposables);
            this.OneWayBind(ViewModel, vm => vm.Adapter, view => view.ModsPanel.IsVisible, static adapter => adapter is not null)
                .AddTo(disposables);
            this.OneWayBind(ViewModel, vm => vm.HasInstalledTab, view => view.InstalledTab.IsVisible)
                .AddTo(disposables);
            this.OneWayBind(ViewModel, vm => vm.Name, view => view.CollectionName.Text)
                .AddTo(disposables);
            this.OneWayBind(ViewModel, vm => vm.StateLine, view => view.Title.Text)
                .AddTo(disposables);
            this.OneWayBind(ViewModel, vm => vm.InstalledModsCount, view => view.NumDownloads.Text, v => $"{v:N0} Mods")
                .AddTo(disposables);
            this.OneWayBind(ViewModel, vm => vm.InstalledModsCount, view => view.InstalledModsCountText.Text, v => $"{v:N0}")
                .AddTo(disposables);
            this.OneWayBind(ViewModel, vm => vm.TileImage, view => view.CollectionImage.Source)
                .AddTo(disposables);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.TileImage))
                .Subscribe(image =>
                {
                    CollectionImageBorder.IsVisible = image != null;
                    CollectionImage.Source = image;
                })
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.AuthorAvatar, view => view.AuthorAvatar.Source)
                .AddTo(disposables);
            this.OneWayBind(ViewModel, vm => vm.VersionText, view => view.Revision.Text)
                .DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.AuthorName, view => view.AuthorName.Text)
                .AddTo(disposables);
            this.BindCommand(ViewModel, vm => vm.CommandToggle, view => view.CollectionToggle)
                .AddTo(disposables);
            this.OneWayBind(ViewModel, vm => vm.IsToggleVisible, view => view.CollectionToggle.IsVisible)
                .AddTo(disposables);

            this.OneWayBind(ViewModel, vm => vm.Downloads, view => view.DownloadsTab.IsVisible, static downloads => HasDownloadsTab(downloads))
                .AddTo(disposables);
            this.OneWayBind(ViewModel, vm => vm.Downloads, view => view.DownloadsHost.ViewModel)
                .AddTo(disposables);

            this.OneWayBind(ViewModel, vm => vm.Instructions, view => view.InstructionsTab.IsVisible, static instructions => HasInstructionsTab(instructions))
                .AddTo(disposables);
            this.OneWayBind(ViewModel, vm => vm.Instructions, view => view.InstructionsContent.Instructions)
                .AddTo(disposables);

            this.Bind(ViewModel, vm => vm.SelectedTab, view => view.TabControl.SelectedItem,
                    tab => tab switch
                    {
                        CollectionPageTab.Downloads => DownloadsTab,
                        CollectionPageTab.Instructions => InstructionsTab,
                        _ => InstalledTab,
                    },
                    item => ReferenceEquals(item, DownloadsTab) ? CollectionPageTab.Downloads
                        : ReferenceEquals(item, InstructionsTab) ? CollectionPageTab.Instructions
                        : CollectionPageTab.Installed)
                .AddTo(disposables);

            this.OneWayBind(ViewModel, vm => vm.IsReadOnly, view => view.ReadOnlyPillStack.IsVisible)
                .AddTo(disposables);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.IsCollectionEnabled))
                .WhereNotNull()
                .SubscribeWithErrorLogging(value =>
                    {
                        CollectionToggle.IsChecked = value;
                    }
                )
                .DisposeWith(disposables);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.BackgroundImage))
                .WhereNotNull()
                .SubscribeWithErrorLogging(image => HeaderImageBorder.Background = new ImageBrush { Source = image, Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top})
                .DisposeWith(disposables);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.MenuItems))
                .Subscribe(FillMoreFlyout)
                .DisposeWith(disposables);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.Description))
                .Subscribe(text =>
                {
                    DescriptionText.Text = text;
                    DescriptionPanel.IsVisible = text != "";
                    UpdateDescriptionToggle();
                })
                .DisposeWith(disposables);

            // Whether the text gets cut off depends on the width it wraps at, so it is decided from the laid-out text.
            DescriptionText.LayoutUpdated += OnDescriptionLayoutUpdated;
            Disposable.Create(() => DescriptionText.LayoutUpdated -= OnDescriptionLayoutUpdated).DisposeWith(disposables);
            DescriptionToggle.Click += OnDescriptionToggleClick;
            Disposable.Create(() => DescriptionToggle.Click -= OnDescriptionToggleClick).DisposeWith(disposables);

            if (ViewModel is { } viewModel) ShowStaticHeader(viewModel, disposables);
        });
    }

    // The header fields a collection keeps for the life of its page: which numbers it has, who made it, where it
    // comes from and what the toolbar's link says.
    private void ShowStaticHeader(ICollectionLoadoutViewModel viewModel, CompositeDisposable disposables)
    {
        var stats = viewModel.Stats;
        OverallRatingPanel.IsVisible = stats.Rating is not null;
        EndorsementsPanel.IsVisible = stats.Endorsements is not null;
        DownloadsPanel.IsVisible = stats.Downloads is not null;
        TotalSizePanel.IsVisible = stats.TotalSize is not null;
        ArchivesPanel.IsVisible = stats.ArchiveCount is not null;

        var rating = stats.Rating?.Value ?? 0;
        var ratingClass = rating switch
        {
            >= 0.75 => "HighRating",
            >= 0.5 => "MidRating",
            >= 0.01 => "LowRating",
            _ => "NoRating",
        };
        OverallRatingPanel.Classes.Add(ratingClass);
        OverallRating.Text = ratingClass == "NoRating" ? "--" : rating.ToString("P0");
        if (stats.Endorsements is { } endorsements) Endorsements.Text = Convert.ToInt32(endorsements).ToMetric(null, 1);
        if (stats.Downloads is { } downloads) TotalDownloads.Text = Convert.ToInt32(downloads).ToMetric(null, 1);
        if (stats.TotalSize is { } size) TotalSize.Text = size.ToString();
        if (stats.ArchiveCount is { } archives) ArchiveCountText.Text = $"{archives:N0}";

        var hasAuthor = viewModel.AuthorName != "";
        var hasVersion = viewModel.VersionText != "";
        AuthorStackPanel.IsVisible = hasAuthor;
        RevisionsPanel.IsVisible = hasVersion;
        VersionSeparator.IsVisible = hasAuthor && hasVersion;

        // The badge's and the link's texts, icons and command are bound in the XAML.
        BadgeStack.IsVisible = viewModel.Badge is not null;
        if (viewModel.Link is { } link)
        {
            // The link's visibility may come from a database thread.
            link.IsVisible.ObserveOn(RxApp.MainThreadScheduler).Subscribe(visible => LinkButton.IsVisible = visible).DisposeWith(disposables);
        }
        else
        {
            LinkButton.IsVisible = false;
        }
    }

    private void FillMoreFlyout(IReadOnlyList<IContextMenuItem> items)
    {
        if (MoreButton.Flyout is not MenuFlyout flyout) return;

        flyout.Items.Clear();
        foreach (var item in items.Where(static item => item.IsVisible))
            flyout.Items.Add(item.ToMenuItem());
    }

    private const int CollapsedLines = 3;

    private void OnDescriptionLayoutUpdated(object? sender, EventArgs e) => UpdateDescriptionToggle();

    private void OnDescriptionToggleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var expand = DescriptionText.MaxLines != 0;
        DescriptionText.MaxLines = expand ? 0 : CollapsedLines;
        DescriptionToggle.Text = expand ? "Show less" : "Show more";
    }

    // The lines the text takes at the width it is laid out in, however many the TextBlock is currently allowed to show.
    private int WrappedLineCount()
    {
        var width = DescriptionText.Bounds.Width;
        if (width <= 0 || string.IsNullOrEmpty(DescriptionText.Text)) return 0;
        var typeface = new Typeface(DescriptionText.FontFamily, DescriptionText.FontStyle, DescriptionText.FontWeight, DescriptionText.FontStretch);
        var layout = new TextLayout(DescriptionText.Text, typeface, DescriptionText.FontSize, DescriptionText.Foreground,
            textWrapping: TextWrapping.Wrap, maxWidth: width, maxHeight: double.PositiveInfinity);
        return layout.TextLines.Count;
    }

    // "Show more" is only worth offering when the collapsed text is actually cut off; once expanded it stays so the
    // text can be collapsed again.
    private void UpdateDescriptionToggle()
    {
        var expanded = DescriptionText.MaxLines == 0;
        var cutOff = WrappedLineCount() > CollapsedLines;
        DescriptionToggle.IsVisible = DescriptionPanel.IsVisible && (expanded || cutOff);
    }

    private static bool HasInstructionsTab(CollectionInstructions instructions)
    {
        return CollectionPageTabs.For(hasDownloadPage: false, hasInstructions: instructions.HasAny).Contains(CollectionPageTab.Instructions);
    }

    private static bool HasDownloadsTab(IPageViewModelInterface? downloads)
    {
        return CollectionPageTabs.For(hasDownloadPage: downloads is not null).Contains(CollectionPageTab.Downloads);
    }
}

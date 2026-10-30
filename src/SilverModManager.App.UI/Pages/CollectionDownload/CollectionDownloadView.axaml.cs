using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Resources;
using SilverModManager.Collections;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using R3;
using ReactiveUI;
using Humanizer;
using ReactiveCommand = R3.ReactiveCommand;

namespace SilverModManager.App.UI.Pages.CollectionDownload;

public partial class CollectionDownloadView : ReactiveUserControl<ICollectionDownloadViewModel>
{
    public CollectionDownloadView()
    {
        InitializeComponent();

        TreeDataGridViewHelper.SetupTreeDataGridAdapter<CollectionDownloadView, ICollectionDownloadViewModel, CompositeItemModel<EntityId>, EntityId>(this, DownloadsTree, vm => vm.TreeDataGridAdapter
        );

        this.WhenActivated(d =>
            {
                // The collection page around an embedded download page already shows the header and the way to the installed mods.
                var isEmbedded = ViewModel?.IsEmbedded ?? throw new InvalidOperationException("The collection download view was activated without a view model");
                HeaderBorder.IsVisible = !isEmbedded;

                this.BindCommand(ViewModel, vm => vm.CommandViewOnNexusMods, view => view.MenuItemViewOnNexusMods)
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.CommandOpenJsonFile, view => view.MenuItemOpenJsonFile)
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.CommandDeleteCollectionRevision, view => view.MenuItemDeleteCollectionRevision)
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.CollectionStatusText, view => view.TextCollectionStatus.Text)
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.CommandViewCollection, view => view.ButtonViewCollection)
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.CommandDownloadRequiredItems, view => view.ButtonDownloadRequiredItems)
                    .DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.CommandInstallRequiredItems, view => view.ButtonInstallRequiredItems)
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.CommandDownloadOptionalItems, view => view.ButtonDownloadOptionalItems)
                    .DisposeWith(d);
                this.BindCommand(ViewModel, vm => vm.CommandInstallOptionalItems, view => view.ButtonInstallOptionalItems)
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.CommandUpdateCollection, view => view.ButtonUpdateCollection)
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.TreeDataGridAdapter.Source.Value, view => view.DownloadsTree.Source)
                    .DisposeWith(d);

                this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.BackgroundImage))
                    .WhereNotNull()
                    .SubscribeWithErrorLogging(image => HeaderImageBorder.Background = new ImageBrush { Source = image, Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top })
                    .DisposeWith(d);

                this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.TileImage))
                    .WhereNotNull()
                    .SubscribeWithErrorLogging(image => CollectionImage.Source = image)
                    .DisposeWith(d);

                this.WhenViewModelValue(vm => vm.WhenAnyValue(
                        x => x.IsDownloading.Value,
                        x => x.IsInstalling.Value,
                        static (isDownloading, isInstalling) => isDownloading || isInstalling
                    ))
                    .Subscribe(isActive => Spinner.IsVisible = isActive)
                    .DisposeWith(d);
                
                this.WhenAnyValue(view => view.ViewModel)
                    .WhereNotNull()
                    .Select(static vm => vm.InstallProgress.AsSystemObservable())
                    .Switch()
                    .Subscribe(ShowInstallProgress)
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.Name, view => view.CollectionName.Text)
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.AuthorName, view => view.AuthorName.Text)
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.AuthorAvatar, view => view.AuthorAvatar.Source)
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.DownloadCount, view => view.NumDownloads.Text,
                        v => $"{v:N0} Mods"
                    )
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.EndorsementCount, view => view.Endorsements.Text,
                        v => Convert.ToInt32(v).ToMetric(null, 1)
                    )
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.TotalDownloads, view => view.TotalDownloads.Text,
                        v => Convert.ToInt32(v).ToMetric(null, 1)
                    )
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.TotalSize, view => view.TotalSize.Text)
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.OverallRating, view => view.OverallRating.Text, p => p.Value.ToString("P0"))
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.RequiredDownloadsCount, view => view.RequiredDownloadsCount.Text,
                        v => v.ToString("N0")
                    )
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.OptionalDownloadsCount, view => view.OptionalDownloadsCount.Text,
                        v => v.ToString("N0")
                    )
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.RevisionNumber, view => view.Revision.Text,
                        revision => $"Revision {revision}"
                    )
                    .DisposeWith(d);

                this.WhenViewModelValue(vm => vm.WhenAnyValue(
                        x => x.IsUpdateAvailable.Value,
                        x => x.NewestRevisionNumber.Value
                    ))
                    .Subscribe(tuple =>
                        {
                            var (isUpdateAvailable, optional) = tuple;

                            ButtonUpdateCollection.IsVisible = isUpdateAvailable;
                            ArrowRight.IsVisible = isUpdateAvailable;
                            NewestRevision.IsVisible = isUpdateAvailable;

                            OlderRevisionBanner.IsVisible = optional.HasValue;
                            if (optional.HasValue)
                            {
                                OlderRevisionBanner.Text = string.Format(Language.CollectionDownloadViewModel_OlderRevision, ViewModel?.RevisionNumber, optional.Value);
                                ButtonUpdateCollection.Text = string.Format(Language.CollectionDownloadViewModel_UpdateCollection, optional.Value);
                                NewestRevision.Text = $"Revision {optional.Value}";
                            }
                        }
                    ).DisposeWith(d);

                this.WhenAnyValue(view => view.ViewModel)
                    .WhereNotNull()
                    .Select(static vm => vm.WillUpdateInstalledRevision.AsSystemObservable())
                    .Switch()
                    .Subscribe(willUpdate => ButtonInstallRequiredItems.Text = willUpdate
                        ? Language.CollectionDownloadViewModel_UpdateInstalledCollection
                        : Language.CollectionDownloadViewModel_InstallCollection)
                    .DisposeWith(d);

                this.WhenAnyValue(view => view.ViewModel)
                    .WhereNotNull()
                    .Select(static vm => vm.IsOlderThanInstalledRevision.AsSystemObservable())
                    .Switch()
                    .Subscribe(isOlder => ToolTip.SetTip(ButtonInstallRequiredItems, isOlder ? Language.CollectionDownloadViewModel_OlderThanInstalled : null))
                    .DisposeWith(d);

                this.WhenAnyValue(view => view.TabControl.SelectedItem)
                    .Subscribe(selectedItem =>
                    {
                        CollectionDownloadsFilter filter;
                
                        if (ReferenceEquals(selectedItem, RequiredTab))
                        {
                            filter = CollectionDownloadsFilter.OnlyRequired;
                        } else if (ReferenceEquals(selectedItem, OptionalTab))
                        {
                            filter = CollectionDownloadsFilter.OnlyOptional;
                        } else
                        {
                            return;
                        }
                
                        // Read when the tab changes, not when the page was activated.
                        if (ViewModel is not { } current) return;
                        current.TreeDataGridAdapter.Filter.Value = filter;
                    }).DisposeWith(d);
                
                this.WhenViewModelValue(vm => vm.WhenAnyValue(
                        x => x.CountDownloadedRequiredItems,
                        x => x.CountDownloadedOptionalItems,
                        x => x.IsInstalled.Value,
                        x => x.HasInstalledAllOptionalItems.Value
                        )
                        .CombineLatest(vm.TreeDataGridAdapter.Filter.AsSystemObservable(), (a, b) => (a.Item1, a.Item2, a.Item3, a.Item4, b)))
                    .Subscribe(tuple =>
                    {
                        if (ViewModel is not { } current) return;
                        var (countDownloadedRequiredItems, countDownloadedOptionalItems, isInstalled, hasInstalledAllOptionals, filter) = tuple;
                        var hasDownloadedAllRequiredItems = countDownloadedRequiredItems == current.RequiredDownloadsCount;
                        var hasDownloadedAllOptionalItems = countDownloadedOptionalItems == current.OptionalDownloadsCount;
                
                        ButtonViewCollection.IsVisible = isInstalled && !isEmbedded;
                
                        ButtonDownloadRequiredItems.IsVisible = !hasDownloadedAllRequiredItems;
                        ButtonInstallRequiredItems.IsVisible = !isInstalled && hasDownloadedAllRequiredItems;
                
                        ButtonDownloadOptionalItems.IsVisible = filter == CollectionDownloadsFilter.OnlyOptional && !hasDownloadedAllOptionalItems;
                        ButtonInstallOptionalItems.IsVisible = filter == CollectionDownloadsFilter.OnlyOptional && hasDownloadedAllOptionalItems && !hasInstalledAllOptionals;
                    }).DisposeWith(d);
                
                this.WhenViewModelValue(vm => vm.WhenAnyValue(
                        x => x.OptionalDownloadsCount,
                        x => x.InstructionsRenderer))
                    .Subscribe(tuple =>
                    {
                        var (optionalDownloadsCount, renderer) = tuple;
                        // Embedded, the collection page's own Instructions tab holds them, so this view never has an instructions tab of its own.
                        var hasSingleTab = optionalDownloadsCount == 0 && (renderer is null || isEmbedded);
                
                        if (hasSingleTab) TabControl.Classes.Add("SingleTab");
                        else TabControl.Classes.Remove("SingleTab");
                
                        if (hasSingleTab) TabControl.SelectedItem = RequiredTab;
                        OptionalTab.IsVisible = optionalDownloadsCount != 0;
                    }).DisposeWith(d);
                
                this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.OverallRating))
                    .Select(rating =>
                    {
                        var className = rating.Value switch
                        {
                            >= 0.75 => "HighRating",
                            >= 0.5 => "MidRating",
                            >= 0.01 => "LowRating",
                            _ => "NoRating",
                        };
                        return (className, rating);
                    })
                    .Subscribe(tuple =>
                        {
                            var (className, rating) = tuple;
                            OverallRatingPanel.Classes.Add(className);
                            OverallRating.Text = className == "NoRating" ? "--" : rating.Value.ToString("P0");
                        }
                    )
                    .DisposeWith(d);
                
                this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.CanDownloadAutomatically))
                    .Subscribe(canDownloadAutomatically =>
                    {
                        ButtonDownloadRequiredItems.RequiresPremium = !canDownloadAutomatically;
                        ButtonDownloadOptionalItems.RequiresPremium = !canDownloadAutomatically;
                        
                    }).DisposeWith(d);
                
                
                this.WhenViewModelValue(vm => vm.WhenAnyValue(
                        x => x.InstructionsRenderer,
                        x => x.RequiredModsInstructions,
                        x => x.OptionalModsInstructions))
                    .Subscribe(tuple =>
                    {
                        var (instructionsRenderer, requiredModsInstructions, optionalModsInstructions) = tuple;
                
                        var instructions = new CollectionInstructions(instructionsRenderer, requiredModsInstructions, optionalModsInstructions);
                        InstructionsTab.IsVisible = instructions.HasAny && !isEmbedded;
                        InstructionsContent.Instructions = instructions;
                    }).DisposeWith(d);

                this.WhenViewModelValue(vm => vm.WhenAnyValue(
                    x => x.RequiredDownloadsCount,
                    x => x.OptionalDownloadsCount))
                    .Subscribe(tuple =>
                    {
                        var (requiredDownloadsCount, optionalDownloadsCount) = tuple;

                        RequiredModsEmptyState.IsActive = requiredDownloadsCount == 0;
                        ButtonViewOptionalMods.Text = $"View optional mods ({optionalDownloadsCount})";

                        if (requiredDownloadsCount != 0) RequiredTab.Content = TabItemContent;
                    }).AddTo(d);
            }
        );

        ButtonViewOptionalMods.Command = new ReactiveCommand(_ => TabControl.SelectedItem = OptionalTab);
    }

    private void ShowInstallProgress(CollectionInstallProgress.Snapshot? snapshot)
    {
        InstallProgressPanel.IsVisible = snapshot is not null;
        if (snapshot is null) return;

        TextInstallSummary.Text = CollectionInstallProgressText.Summary(snapshot);
        TextInstallRunning.Text = CollectionInstallProgressText.Installing(snapshot);
        TextInstallRunning.IsVisible = snapshot.Installing.Count > 0;
        TextInstallDisabled.Text = CollectionInstallProgressText.DisabledNotice(snapshot);
        TextInstallDisabled.IsVisible = TextInstallDisabled.Text.Length > 0;
        ListInstallFailures.ItemsSource = CollectionInstallProgressText.FailedLines(snapshot);
    }
}

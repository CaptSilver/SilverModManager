using System.ComponentModel;
using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using NexusMods.Abstractions.Games;
using NexusMods.App.UI.Controls;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.Resources;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.Sorting;

public partial class LoadOrderView : ReactiveUserControl<ILoadOrderViewModel>
{
    public LoadOrderView()
    {
        InitializeComponent();
        
        TreeDataGridViewHelper.SetupTreeDataGridAdapter<LoadOrderView, ILoadOrderViewModel, CompositeItemModel<ISortItemKey>, ISortItemKey>(
            this,
            SortOrderTreeDataGrid,
            vm => vm.Adapter,
            enableDragAndDrop: true
        );

        this.WhenActivated(disposables =>
            {
                // TreeDataGrid Source
                this.OneWayBind(ViewModel,
                        vm => vm.Adapter.Source.Value,
                        view => view.SortOrderTreeDataGrid.Source
                    )
                    .DisposeWith(disposables);

                // Trophy bar
                this.WhenAnyValue(view => view.ViewModel!.IsWinnerTop)
                    .Subscribe(isWinnerTop =>
                        {
                            DockPanel.SetDock(TrophyIcon, isWinnerTop ? Dock.Top : Dock.Bottom);
                            // not used anymore for styling but leaving these in just in case
                            TrophyBarDockPanel.Classes.ToggleIf("IsWinnerTop", isWinnerTop);
                            TrophyBarDockPanel.Classes.ToggleIf("IsWinnerBottom", !isWinnerTop);
                        }
                    )
                    .DisposeWith(disposables);

                // Trophy bar arrow
                this.WhenAnyValue(view => view.ViewModel!.SortDirectionCurrent)
                    .Subscribe(sortCurrentDirection =>
                        {
                            var isAscending = sortCurrentDirection == ListSortDirection.Ascending;
                            ArrowUpIcon.IsVisible = !isAscending;
                            ArrowDownIcon.IsVisible = isAscending;
                        }
                    )
                    .DisposeWith(disposables);

                // trophy tooltip
                this.WhenAnyValue(view => view.ViewModel!.TrophyToolTip)
                    .Subscribe(tooltip => { ToolTip.SetTip(TrophyBarDockPanel, tooltip); })
                    .DisposeWith(disposables);

                // Empty state
                this.OneWayBind(ViewModel,
                        vm => vm.Adapter.IsSourceEmpty.Value,
                        view => view.EmptyState.IsActive
                    )
                    .DisposeWith(disposables);

                // Empty state Header
                this.OneWayBind(ViewModel,
                        vm => vm.EmptyStateMessageTitle,
                        view => view.EmptyState.Header
                    )
                    .DisposeWith(disposables);

                // Empty state Message
                this.OneWayBind(ViewModel,
                        vm => vm.EmptyStateMessageContents,
                        view => view.EmptySpaceMessageTextBlock.Text
                    )
                    .DisposeWith(disposables);

                // alert title
                this.OneWayBind(ViewModel,
                        vm => vm.InfoAlertTitle,
                        view => view.LoadOrderAlert.Title
                    )
                    .DisposeWith(disposables);

                // alert body
                this.OneWayBind(ViewModel,
                        vm => vm.InfoAlertBody,
                        view => view.LoadOrderAlert.Body
                    )
                    .DisposeWith(disposables);

                // Alert settings
                this.OneWayBind(ViewModel, vm => vm.AlertSettingsWrapper,
                        view => view.LoadOrderAlert.AlertSettings
                    )
                    .DisposeWith(disposables);

                // Alert toggle (help button next to tree data grid)
                this.OneWayBind(ViewModel, vm => vm.ToggleAlertCommand,
                        view => view.InfoAlertButton.Command
                    )
                    .DisposeWith(disposables);

                // Alert toggle (x button on alert)
                this.OneWayBind(ViewModel, vm => vm.ToggleAlertCommand,
                        view => view.AlertDismissButton.Command
                    )
                    .DisposeWith(disposables);
                
                // Replaced-edit banner
                this.OneWayBind(ViewModel, vm => vm.HasReplacedEdit,
                        view => view.ReplacedEditAlert.IsVisible
                    )
                    .DisposeWith(disposables);

                this.OneWayBind(ViewModel, vm => vm.ReplacedEditSummary,
                        view => view.ReplacedEditAlert.Body
                    )
                    .DisposeWith(disposables);

                this.OneWayBind(ViewModel, vm => vm.DismissReplacedEditCommand,
                        view => view.ReplacedEditDismissButton.Command
                    )
                    .DisposeWith(disposables);

                ReplacedEditAlert.Title = Language.LoadOrder_ReplacedEdit_Title;
                ReplacedEditDismissButton.Text = Language.LoadOrder_ReplacedEdit_Dismiss;

                // Sort with LOOT
                this.OneWayBind(ViewModel, vm => vm.SupportsLootSort,
                        view => view.SortWithLootButton.IsVisible
                    )
                    .DisposeWith(disposables);

                this.OneWayBind(ViewModel, vm => vm.SortWithLootCommand,
                        view => view.SortWithLootButton.Command
                    )
                    .DisposeWith(disposables);

                this.OneWayBind(ViewModel, vm => vm.HasLootOutcome,
                        view => view.LootOutcomeAlert.IsVisible
                    )
                    .DisposeWith(disposables);

                this.OneWayBind(ViewModel, vm => vm.LootOutcomeTitle,
                        view => view.LootOutcomeAlert.Title
                    )
                    .DisposeWith(disposables);

                this.OneWayBind(ViewModel, vm => vm.LootOutcomeSummary,
                        view => view.LootOutcomeAlert.Body
                    )
                    .DisposeWith(disposables);

                this.OneWayBind(ViewModel, vm => vm.DismissLootOutcomeCommand,
                        view => view.LootOutcomeDismissButton.Command
                    )
                    .DisposeWith(disposables);

                SortWithLootButton.Text = Language.LoadOrder_SortWithLoot_Button;
                LootOutcomeDismissButton.Text = Language.LoadOrder_SortWithLoot_Dismiss;

                // Alert Learn More
                this.OneWayBind(ViewModel, vm => vm.LearnMoreAlertCommand,
                        view => view.AlertLearnMoreButton.Command
                    )
                    .DisposeWith(disposables);
            }
        );
    }
    
}

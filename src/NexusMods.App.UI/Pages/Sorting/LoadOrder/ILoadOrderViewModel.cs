using System.ComponentModel;
using System.Reactive;
using NexusMods.Abstractions.Games;
using NexusMods.App.UI.Controls;
using NexusMods.App.UI.Controls.Alerts;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.Sorting;

public interface ILoadOrderViewModel : IViewModelInterface
{
    /// <summary>
    /// The variety of the sort order being represented
    /// </summary>
    ISortOrderVariety SortOrderVariety { get; }
    
    /// <summary>
    /// TreeDataGridAdapter for the Load Order, for setting up the TreeDataGrid
    /// </summary>
    TreeDataGridAdapter<CompositeItemModel<ISortItemKey>, ISortItemKey> Adapter { get; }

    /// <summary>
    /// Name of this sort order type
    /// </summary>
    string SortOrderName { get; }
    
    /// <summary>
    /// The title of the alert message, only visible if the alert is visible
    /// </summary>
    string InfoAlertTitle { get; }
    
    /// <summary>
    /// The contents of the alert message, only visible if the alert is visible
    /// </summary>
    string InfoAlertBody { get; }
    
    /// <summary>
    /// Command to toggle the Alert visibility
    /// </summary>
    ReactiveCommand<Unit, Unit> ToggleAlertCommand { get; }
    
    /// <summary>
    /// Command to invoke Learn More
    /// </summary>
    ReactiveCommand<Unit, Unit> LearnMoreAlertCommand { get; }
    
    /// <summary>
    /// Tooltip message contents for the trophy icon
    /// </summary>
    string TrophyToolTip { get; }
    
    /// <summary>
    /// The current ascending/descending direction in which the SortIndexes are sorted and displayed
    /// </summary>
    ListSortDirection SortDirectionCurrent { get; set; }
    
    /// <summary>
    /// Switch the current sort direction to the inverse
    /// </summary>
    ReactiveCommand<Unit, Unit> SwitchSortDirectionCommand { get; }
    
    /// <summary>
    /// True if current sort direction is ascending, false if descending
    /// </summary>
    bool IsAscending { get; }
    
    /// <summary>
    /// Whether the winning item is at the top or bottom of the list
    /// </summary>
    /// <remarks>
    /// Depends on the way the game load order works and can't be deduced exclusively from the sort direction
    /// </remarks>
    bool IsWinnerTop { get; }
    
    /// <summary>
    /// Title text for the empty state, in case there are no sortable items to display
    /// </summary>
    string EmptyStateMessageTitle { get; }
    
    /// <summary>
    /// Contents text for the empty state, in case there are no sortable items to display
    /// </summary>
    string EmptyStateMessageContents { get; }
    
    /// <summary>
    /// True when the rows can be switched on and off, so the page shows a switch and a details column.
    /// </summary>
    bool SupportsToggle { get; }

    /// <summary>
    /// Switches the row with the given key on or off, the opposite of how it is now. Does nothing for a locked row.
    /// </summary>
    ReactiveCommand<ISortItemKey, Unit> ToggleEnabledCommand { get; }

    /// <summary>
    /// What the app replaced when the game's own order file was edited outside it; null when there is no notice.
    /// </summary>
    string? ReplacedEditSummary { get; }

    /// <summary>
    /// True while <see cref="ReplacedEditSummary"/> has something to show.
    /// </summary>
    bool HasReplacedEdit { get; }

    /// <summary>
    /// Acknowledges the replaced-edit notice.
    /// </summary>
    ReactiveCommand<Unit, Unit> DismissReplacedEditCommand { get; }

    /// <summary>
    /// Why the variety stopped writing its list and handed the file back to the game; null when it is writing.
    /// </summary>
    string? HoldSummary { get; }

    /// <summary>
    /// True while <see cref="HoldSummary"/> has something to show.
    /// </summary>
    bool HasHold { get; }

    /// <summary>
    /// Ends the hold so the variety writes the loadout's order again.
    /// </summary>
    ReactiveCommand<Unit, Unit> ReleaseHoldCommand { get; }

    /// <summary>
    /// True when the variety can be sorted by LOOT's rules, so the page shows the Sort with LOOT button.
    /// </summary>
    bool SupportsLootSort { get; }

    /// <summary>
    /// Sorts the order with LOOT's rules and shows what the sort did. A sort that found no valid order also opens a
    /// dialog with the reason.
    /// </summary>
    ReactiveCommand<Unit, Unit> SortWithLootCommand { get; }

    /// <summary>
    /// True while <see cref="LootOutcomeSummary"/> has the result of a sort to show.
    /// </summary>
    bool HasLootOutcome { get; }

    /// <summary>
    /// Heading for the result of the last LOOT sort; null when there is none.
    /// </summary>
    string? LootOutcomeTitle { get; }

    /// <summary>
    /// The masterlist the last LOOT sort used and what it could not apply, or the cycle that stopped it; null when there is none.
    /// </summary>
    string? LootOutcomeSummary { get; }

    /// <summary>
    /// Hides the result of the last LOOT sort.
    /// </summary>
    ReactiveCommand<Unit, Unit> DismissLootOutcomeCommand { get; }

    /// <summary>
    /// AlertSettings wrapper for the Alert so it can be shown\hidden based on the settings
    /// </summary>
    AlertSettingsWrapper AlertSettingsWrapper { get; }
}

using System.Reactive;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Merges;

/// <summary>
/// The merges page of a loadout whose game has a merge engine: every file more than one enabled mod provides, which
/// mods feed it, and what the apply does with it. Read-only; the merges themselves happen on apply.
/// </summary>
public interface IMergesViewModel : IPageViewModelInterface
{
    LoadoutId LoadoutId { get; set; }

    /// <summary>Every path more than one mod provides, as the merge report lists them.</summary>
    IReadOnlyList<MergeReportEntry> Entries { get; }

    /// <summary>The message the apply would stop with, empty when it goes ahead.</summary>
    string Refusal { get; }

    /// <summary>How the user changes the order of the contributors.</summary>
    string PriorityOrderHint { get; }

    /// <summary>Why no report is shown, empty when one is.</summary>
    string Problem { get; }

    /// <summary>What the list says when it has no rows; empty when it has some or has not loaded yet.</summary>
    string EmptyState { get; }

    bool IsLoading { get; }

    MergeReportEntry? Selected { get; set; }

    /// <summary>The notes of <see cref="Selected"/>, followed by the reason it is refused or not merged.</summary>
    IReadOnlyList<string> SelectedNotes { get; }

    ReactiveCommand<Unit, Unit> RefreshCommand { get; }
}

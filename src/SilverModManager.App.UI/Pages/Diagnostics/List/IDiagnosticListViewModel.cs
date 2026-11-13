using System.Collections.ObjectModel;
using System.Reactive;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.App.UI.Controls.Diagnostics;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Diagnostics;

public interface IDiagnosticListViewModel : IPageViewModelInterface
{
    public LoadoutId LoadoutId { get; set; }

    public IDiagnosticEntryViewModel[] DiagnosticEntries { get; }

    public int NumCritical { get; }
    public int NumWarnings { get; }
    public int NumSuggestions { get; }

    public DiagnosticFilter Filter { get; set; }

    /// <summary>
    /// Runs the diagnostics for <see cref="LoadoutId"/> again. The page also does this by itself when it
    /// opens and when the window is activated, so a fix made outside the app shows up without a click.
    /// </summary>
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>
    /// Whether a refresh is running.
    /// </summary>
    public bool IsRefreshing { get; }

    /// <summary>
    /// Writes a support bundle and puts its path on the clipboard.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CopySupportBundleCommand { get; }

    /// <summary>
    /// What the last <see cref="CopySupportBundleCommand"/> run did; empty before the first run.
    /// </summary>
    public string SupportBundleMessage { get; }

    /// <summary>
    /// Whether the loadout's game has something the user can clear from Health Check, such as old crash logs.
    /// </summary>
    public bool CanClear { get; }

    /// <summary>
    /// The text of the clear button, from what there is to clear.
    /// </summary>
    public string ClearLabel { get; }

    /// <summary>
    /// Clears what <see cref="CanClear"/> found, then refreshes the diagnostics.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    /// <summary>
    /// What the last <see cref="ClearCommand"/> run did; empty before the first run.
    /// </summary>
    public string ClearMessage { get; }

    /// <summary>
    /// The findings this loadout ignores, oldest first. Shown at the bottom of the page whether or not anything is reported.
    /// </summary>
    public IReadOnlyList<IgnoredFinding> IgnoredFindings { get; }

    /// <summary>
    /// Stops ignoring the finding whose <see cref="IgnoredFinding.Row"/> is the argument, then refreshes the diagnostics.
    /// </summary>
    public ReactiveCommand<EntityId, Unit> ShowAgainCommand { get; }
}

/// <summary>
/// One row of the ignored list.
/// </summary>
/// <param name="Title">The title of the diagnostic when it was ignored.</param>
/// <param name="Subject">What was ignored, such as <c>Game|Data/SKSE/Plugins/x.dll</c>.</param>
/// <param name="When">When the player ignored it.</param>
/// <param name="Row">The dismissal's entity id, which <see cref="IDiagnosticListViewModel.ShowAgainCommand"/> deletes.</param>
public sealed record IgnoredFinding(string Title, string Subject, DateTimeOffset When, EntityId Row);

[Flags]
public enum DiagnosticFilter
{
    None = 0,
    Critical = 1 << 0,
    Warnings = 1 << 1,
    Suggestions = 1 << 2,
}

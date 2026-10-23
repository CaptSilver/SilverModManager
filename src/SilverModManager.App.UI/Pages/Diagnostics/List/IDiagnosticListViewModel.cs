using System.Collections.ObjectModel;
using System.Reactive;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Ids;
using NexusMods.App.UI.Controls.Diagnostics;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Sdk.Loadouts;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.Diagnostics;

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
}

[Flags]
public enum DiagnosticFilter
{
    None = 0,
    Critical = 1 << 0,
    Warnings = 1 << 1,
    Suggestions = 1 << 2,
}

using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Controls.Diagnostics;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>What identifies a row of the Health Check list across a refresh. A <see cref="DiagnosticId"/> only names the kind of diagnostic, so the text decides too.</summary>
internal readonly record struct DiagnosticRowKey(DiagnosticId Id, string Title, string Summary);

/// <summary>Decides whether a refreshed Health Check list keeps the place the user scrolled to.</summary>
internal static class DiagnosticListScroll
{
    /// <summary>True when the refresh shows the same rows in the same order, so the user is still looking at the same list; any other list starts at the top.</summary>
    public static bool KeepsOffset(IReadOnlyList<DiagnosticRowKey> previous, IReadOnlyList<DiagnosticRowKey> next) => previous.SequenceEqual(next);

    public static DiagnosticRowKey[] KeysOf(IEnumerable<IDiagnosticEntryViewModel> entries) =>
        entries.Select(entry => new DiagnosticRowKey(entry.Diagnostic.Id, entry.Title, entry.Summary)).ToArray();
}

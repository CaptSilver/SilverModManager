using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.DataModel.CommandLine.Verbs;

/// <summary>
/// Verbs that report on the problems the Health Check page shows for a loadout, and run the fixes it offers.
/// </summary>
public static class DiagnosticVerbs
{
    // The "loadout" module comes from AddLoadoutManagementVerbs.
    public static IServiceCollection AddDiagnosticVerbs(this IServiceCollection collection) => collection
        .AddModule("loadout diagnostic", "Commands for the fixes the Health Check offers for a loadout")
        .AddVerb(() => ListDiagnostics)
        .AddVerb(() => FixDiagnostic)
        .AddVerb(() => ClearDiagnostics);

    // Handled is false when this process has no handler for the action: a terminal run with the window closed is
    // answered by a headless process, and the handlers live with the window.
    private sealed record FixRow(string Id, string Label, string? Note, bool CanRunHere, [property: JsonIgnore] bool Handled);

    private sealed record DiagnosticRow(int Index, string Id, string Severity, string Title, string Summary, string Details, FixRow[] Fixes);

    [Verb("loadout diagnostics", "Lists the problems and suggestions the Health Check finds for a loadout, with the index 'loadout diagnostic fix -d' takes and the fixes each offers")]
    private static async Task<int> ListDiagnostics([Injected] IRenderer renderer,
        [Injected] IDiagnosticManager diagnosticManager,
        [Injected] IDiagnosticWriter writer,
        [Injected] IEnumerable<IDiagnosticActionHandler> handlers,
        [Option("l", "loadout", "Loadout to check")] Loadout.ReadOnly loadout,
        [Option("r", "refresh", "Run the checks again first, to pick up changes made outside the app", true)] bool refresh,
        [Option("j", "json", "Print the diagnostics as JSON", true)] bool json)
    {
        if (refresh) await diagnosticManager.RefreshAsync(loadout.LoadoutId);

        var handlerList = handlers.ToArray();
        var diagnostics = await diagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).FirstAsync();
        var rows = diagnostics
            .Select((d, index) => new DiagnosticRow(index, d.Id.ToString(), d.Severity.ToString(), d.Title,
                d.FormatSummary(writer), d.FormatDetails(writer, DiagnosticWriterMode.Markdown),
                d.Actions.IsDefaultOrEmpty
                    ? []
                    : d.Actions.Select(a => FixRowFor(handlerList, a)).ToArray()))
            .ToArray();

        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        if (rows.Length == 0)
        {
            await renderer.TextLine("No problems were found.");
            return 0;
        }

        foreach (var row in rows)
        {
            await renderer.TextLine($"[{row.Index}] {row.Severity}: {row.Title}");
            await renderer.TextLine(row.Summary);
            await renderer.TextLine(row.Details);
            foreach (var fix in row.Fixes)
            {
                var how = fix switch
                {
                    { CanRunHere: true } => $"loadout diagnostic fix -l {loadout.LoadoutId} -d {row.Index} -a {fix.Id}",
                    { Handled: true } => "needs the Health Check page",
                    _ => "no command line fix, use the Health Check page",
                };
                await renderer.TextLine($"Fix: {fix.Label} ({how})");
            }
            await renderer.TextLine("");
        }
        return 0;
    }

    [Verb("loadout diagnostic fix", "Runs a fix the Health Check offers, as its button does. -d is the index 'loadout diagnostics' prints, or the diagnostic's id when only one has it. A restore point is written first, see 'loadout revisions'. Fixes that ask a question in the app are refused here")]
    private static async Task<int> FixDiagnostic([Injected] IRenderer renderer,
        [Injected] IDiagnosticManager diagnosticManager,
        [Injected] IEnumerable<IDiagnosticActionHandler> handlers,
        [Injected] IConnection connection,
        [Option("l", "loadout", "Loadout to fix")] Loadout.ReadOnly loadout,
        [Option("d", "diagnostic", "The diagnostic's index, or its id")] string selector,
        [Option("a", "action", "The fix to run, by the id 'loadout diagnostics' prints; needed only when the diagnostic offers several", true)] string? actionId)
    {
        var diagnostics = await diagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).FirstAsync();
        var diagnostic = Select(diagnostics, selector);
        var action = SelectAction(diagnostic, actionId);

        var handler = HandlerFor(handlers, action)
                      ?? throw new LoadoutVerbRefusedException($"{MarkdownText.Code(action.Label)} ({MarkdownText.Code(action.Id)}) has no command line fix. Run it from the Health Check page in SilverModManager.");
        if (!handler.RunsUnattended)
            throw new LoadoutVerbRefusedException($"{MarkdownText.Code(handler.LabelFor(action) ?? action.Label)} asks for confirmation or shows its result in a window, so run it from the Health Check page.");

        // Written before the fix so that reverting to it undoes whatever the fix changes.
        await LoadoutSnapshot.WriteRestorePoint(connection, loadout.LoadoutId);
        await handler.ExecuteAsync(loadout.LoadoutId, action.Arguments);

        await renderer.TextLine($"Ran {action.Label}. 'loadout revisions' lists the restore point; 'loadout revert' undoes it.");
        return 0;
    }

    [Verb("loadout diagnostic clear", "Clears what the Health Check offers to clear, such as old crash logs, then runs the checks again")]
    private static async Task<int> ClearDiagnostics([Injected] IRenderer renderer,
        [Injected] IDiagnosticManager diagnosticManager,
        [Option("l", "loadout", "Loadout to clear for")] Loadout.ReadOnly loadout,
        [Injected] CancellationToken token)
    {
        var actions = await Task.Run(() => diagnosticManager.GetClearActions(loadout.LoadoutId), token);
        if (actions.Length == 0)
        {
            await renderer.TextLine("Nothing to clear.");
            return 0;
        }

        // Every action runs even when an earlier one fails, as on the Health Check page.
        var failed = false;
        foreach (var action in actions)
        {
            try
            {
                await renderer.TextLine(await action.ClearAsync(token));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed = true;
                await renderer.TextLine($"Could not clear {action.Label}: {ex.Message}");
            }
        }

        await diagnosticManager.RefreshAsync(loadout.LoadoutId);
        return failed ? 1 : 0;
    }

    private static FixRow FixRowFor(IDiagnosticActionHandler[] handlers, DiagnosticAction action)
    {
        var handler = HandlerFor(handlers, action);
        return new FixRow(action.Id, action.Label, action.Note, handler?.RunsUnattended == true, handler is not null);
    }

    private static IDiagnosticActionHandler? HandlerFor(IEnumerable<IDiagnosticActionHandler> handlers, DiagnosticAction action)
        => handlers.FirstOrDefault(h => h.ActionId == action.Id);

    private static Diagnostic Select(Diagnostic[] diagnostics, string selector)
    {
        if (int.TryParse(selector, out var index))
        {
            if (index >= 0 && index < diagnostics.Length) return diagnostics[index];
            throw new LoadoutVerbRefusedException($"There is no diagnostic with index {index}: the loadout has {diagnostics.Length}. 'loadout diagnostics' lists them with their indexes.");
        }

        var matches = diagnostics.Select((d, i) => (Diagnostic: d, Index: i)).Where(m => string.Equals(m.Diagnostic.Id.ToString(), selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0].Diagnostic,
            0 => throw new LoadoutVerbRefusedException($"No diagnostic has the id {MarkdownText.Code(selector)}. 'loadout diagnostics' lists them."),
            _ => throw new LoadoutVerbRefusedException($"{matches.Length} diagnostics have the id {MarkdownText.Code(selector)} (indexes {string.Join(", ", matches.Select(m => m.Index))}). Pass one of those indexes to -d instead."),
        };
    }

    private static DiagnosticAction SelectAction(Diagnostic diagnostic, string? actionId)
    {
        if (diagnostic.Actions.IsDefaultOrEmpty)
            throw new LoadoutVerbRefusedException($"{MarkdownText.Code(diagnostic.Title)} has no automatic fix; its details say what to do by hand.");

        if (actionId is null)
        {
            if (diagnostic.Actions.Length == 1) return diagnostic.Actions[0];
            throw new LoadoutVerbRefusedException($"{MarkdownText.Code(diagnostic.Title)} offers several fixes ({string.Join(", ", diagnostic.Actions.Select(a => a.Id))}). Pick one with -a.");
        }

        var chosen = diagnostic.Actions.Where(a => a.Id == actionId).ToArray();
        return chosen.Length > 0
            ? chosen[0]
            : throw new LoadoutVerbRefusedException($"{MarkdownText.Code(diagnostic.Title)} has no fix called {MarkdownText.Code(actionId)}; it offers {string.Join(", ", diagnostic.Actions.Select(a => a.Id))}.");
    }
}

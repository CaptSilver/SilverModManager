using System.Reactive.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.DataModel.CommandLine.Verbs;

/// <summary>
/// Verbs that report on the problems the Health Check page shows for a loadout.
/// </summary>
public static class DiagnosticVerbs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // The "loadout" module comes from AddLoadoutManagementVerbs.
    public static IServiceCollection AddDiagnosticVerbs(this IServiceCollection collection) =>
        collection.AddVerb(() => ListDiagnostics);

    private sealed record DiagnosticRow(string Id, string Severity, string Title, string Summary, string Details);

    [Verb("loadout diagnostics", "Lists the problems and suggestions the Health Check finds for a loadout")]
    private static async Task<int> ListDiagnostics([Injected] IRenderer renderer,
        [Injected] IDiagnosticManager diagnosticManager,
        [Injected] IDiagnosticWriter writer,
        [Option("l", "loadout", "Loadout to check")] Loadout.ReadOnly loadout,
        [Option("r", "refresh", "Run the checks again first, to pick up changes made outside the app", true)] bool refresh,
        [Option("j", "json", "Print the diagnostics as JSON", true)] bool json)
    {
        if (refresh) await diagnosticManager.RefreshAsync(loadout.LoadoutId);

        var diagnostics = await diagnosticManager.GetLoadoutDiagnostics(loadout.LoadoutId).FirstAsync();
        var rows = diagnostics
            .Select(d => new DiagnosticRow(d.Id.ToString(), d.Severity.ToString(), d.Title,
                d.FormatSummary(writer), d.FormatDetails(writer, DiagnosticWriterMode.Markdown)))
            .ToArray();

        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(rows, JsonOptions));
            return 0;
        }

        if (rows.Length == 0)
        {
            await renderer.TextLine("No problems were found.");
            return 0;
        }

        foreach (var row in rows)
        {
            await renderer.TextLine($"{row.Severity}: {row.Title}");
            await renderer.TextLine(row.Summary);
            await renderer.TextLine(row.Details);
            await renderer.TextLine("");
        }
        return 0;
    }
}

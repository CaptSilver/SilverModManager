using System.Text.Json;
using System.Text.Json.Serialization;
using NexusMods.Abstractions.Cli;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.ProxyConsole;
using Table = NexusMods.Sdk.ProxyConsole.Table;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>The command line's view of <see cref="IMergeReports"/>. It only reads: the apply does the merging.</summary>
internal static class MergeVerbs
{
    internal const string ListName = "fromsoft merge list";
    internal const string EmptyMessage = "No path is provided by more than one mod, so there is nothing to merge.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] Columns = ["Path", "Strategy", "Outcome", "Contributors", "Vanilla source", "Reason"];

    [Verb(ListName, "Lists the files the app merges for a loadout, which mods feed each one, and whether an apply would be refused")]
    internal static async Task<int> List(
        [Injected] IRenderer renderer,
        [Injected] IMergeReports reports,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout to report on")] Loadout.ReadOnly loadout,
        [Option("j", "json", "Print the report as JSON", true)] bool json)
    {
        var report = await reports.ForLoadout(loadout.LoadoutId, token);
        // A refused apply is the one result a script needs to see without parsing anything.
        var exitCode = report.Refusal is null ? 0 : 1;

        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(report, JsonOptions));
            return exitCode;
        }

        if (report.Refusal is { } refusal) await renderer.TextLine(refusal);

        if (report.Entries.Count == 0)
        {
            if (report.Refusal is null) await renderer.TextLine(EmptyMessage);
            return exitCode;
        }

        await renderer.RenderAsync(new Table
        {
            Columns = Columns.Select(c => (IRenderable)Renderable.Text(c)).ToArray(),
            Rows = report.Entries.Select(Row).ToArray(),
        });

        foreach (var entry in report.Entries.Where(e => e.Notes.Count > 0))
        {
            await renderer.TextLine(entry.Path + ":");
            foreach (var note in entry.Notes) await renderer.TextLine("  " + note);
        }

        await renderer.TextLine(report.PriorityOrderHint);
        return exitCode;
    }

    private static IRenderable[] Row(MergeReportEntry entry)
        =>
        [
            Renderable.Text(entry.Path),
            Renderable.Text(entry.Strategy),
            Renderable.Text(entry.Outcome.Label()),
            Renderable.Text(string.Join(", ", entry.Contributors)),
            Renderable.Text(entry.VanillaSource),
            Renderable.Text(entry.Reason),
        ];
}

using System.Text.Json;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Verbs;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using Table = SilverModManager.Sdk.ProxyConsole.Table;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Pins which mod wins one contested entry of a merged file. The pin is a <see cref="LoadoutMergeChoice"/> row that
/// the next apply reads; <c>loadout revert</c> undoes it like any other loadout change.
/// </summary>
internal static class MergeChoiceVerbs
{
    internal const string ListName = "fromsoft merge choices list";
    internal const string SetName = "fromsoft merge choices set";
    internal const string ClearName = "fromsoft merge choices clear";
    internal const string ListFooter = "A choice is applied on the next Apply; the winner is the mod whose edit the merged file keeps";

    private static readonly string[] Columns = ["Path", "Entry", "Candidates", "Winner", "Source"];

    /// <summary>One line of <c>list --json</c>. <c>ChoiceId</c> is set when a choice is stored for the entry.</summary>
    internal sealed record ChoiceRow(string Path, string Key, string[] Candidates, string Winner, bool Chosen, string? ChoiceId);

    [Verb(ListName, "Lists the contested entries of a loadout's merged files, the mod that wins each and whether a choice or the load order decided it")]
    internal static async Task<int> List(
        [Injected] IRenderer renderer,
        [Injected] IMergeReports reports,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout to list")] Loadout.ReadOnly loadout,
        [Option("p", "path", "Only this merged file, as 'fromsoft merge list' prints it", true)] string? path,
        [Option("j", "json", "Print the rows as JSON", true)] bool json)
    {
        var report = await reports.ForLoadout(loadout.LoadoutId, token);
        var rows = Rows(loadout, report, path);

        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(rows.Select(r => r.Row), CliJson.Options));
            return 0;
        }

        if (rows.Count == 0)
        {
            await renderer.TextLine("No entry of a merged file is contested, so there is nothing to choose.");
            return 0;
        }

        await renderer.RenderAsync(new Table
        {
            Columns = Columns.Select(c => (IRenderable)Renderable.Text(c)).ToArray(),
            Rows = rows.Select(r => new IRenderable[]
            {
                Renderable.Text(r.Row.Path),
                Renderable.Text(r.Row.Key),
                Renderable.Text(string.Join(", ", r.Row.Candidates)),
                Renderable.Text(r.Row.Winner),
                Renderable.Text(r.Source),
            }).ToArray(),
        });
        await renderer.TextLine(ListFooter);
        return 0;
    }

    [Verb(SetName, "Makes one mod win a contested entry of a merged file, whatever the load order says. A restore point is written first, see 'loadout revisions'.")]
    internal static async Task<int> Set(
        [Injected] IRenderer renderer,
        [Injected] IMergeReports reports,
        [Injected] IConnection connection,
        [Injected] MergeChoiceStore store,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout the choice belongs to")] Loadout.ReadOnly loadout,
        [Option("p", "path", "The merged file, as 'fromsoft merge list' prints it")] string path,
        [Option("e", "entry", "The entry, as 'fromsoft merge choices list' prints it, e.g. param:12/3400")] string entry,
        [Option("w", "winner", "The mod that should win: its name, or its id from 'loadout groups list --json' when two share a name")] string winnerName)
    {
        RefuseUnchoosable(entry);

        var report = await reports.ForLoadout(loadout.LoadoutId, token);
        var merged = report.Entries.FirstOrDefault(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            ?? throw new LoadoutVerbRefusedException($"{MarkdownText.Code(path)} is not a merged file of this loadout. 'fromsoft merge list' shows the merged files.");
        var contest = merged.Contests.FirstOrDefault(c => c.Key == entry)
            ?? throw new LoadoutVerbRefusedException($"Nothing contests {MarkdownText.Code(entry)} of {MarkdownText.Code(merged.Path)}; 'fromsoft merge choices list' shows what does.");

        var winner = LoadoutGroupLookup.Find(loadout, winnerName);
        var name = winner.AsLoadoutItem().Name;
        if (!contest.Candidates.Contains(name, StringComparer.Ordinal))
            throw new LoadoutVerbRefusedException($"{MarkdownText.Code(name)} does not edit {MarkdownText.Code(entry)} of {MarkdownText.Code(merged.Path)}. The candidates are {string.Join(", ", contest.Candidates.Select(MarkdownText.Code))}.");

        await LoadoutSnapshot.WriteRestorePoint(connection, loadout.LoadoutId);
        await store.Upsert(loadout.LoadoutId, merged.Path, entry, winner, MergeChoices.NexusModIdOf(loadout.Db, LoadoutItemGroupId.From(winner.Id)));
        await renderer.TextLine("{0}", $"{name} now wins {entry} of {merged.Path}; apply the loadout for it to take effect. 'loadout revert' undoes it.");
        return 0;
    }

    [Verb(ClearName, "Removes one choice, or with --all every choice of the loadout, so the load order decides again. A restore point is written first, see 'loadout revisions'.")]
    internal static async Task<int> Clear(
        [Injected] IRenderer renderer,
        [Injected] IConnection connection,
        [Injected] MergeChoiceStore store,
        [Option("l", "loadout", "The loadout to clear")] Loadout.ReadOnly loadout,
        [Option("p", "path", "The merged file; alone with --all it limits the removal to that file", true)] string? path,
        [Option("e", "entry", "The entry to clear, as 'fromsoft merge choices list' prints it", true)] string? entry,
        [Option("a", "all", "Remove every choice, of the loadout or of --path", true)] bool all)
    {
        if (entry is null && !all)
            throw new LoadoutVerbRefusedException("Name the choice to clear with --path and --entry, or pass --all to clear every choice.");
        if (entry is not null && path is null)
            throw new LoadoutVerbRefusedException("--entry needs --path: the same entry can be contested in more than one merged file.");

        await LoadoutSnapshot.WriteRestorePoint(connection, loadout.LoadoutId);
        var removed = entry is not null && path is not null
            ? await store.Delete(loadout.LoadoutId, path, entry)
            : await store.Clear(loadout.LoadoutId, path);
        await renderer.TextLine("{0}", removed == 0
            ? "There was no choice to clear."
            : $"Cleared {removed} {(removed == 1 ? "choice" : "choices")}; the load order decides those entries again after the next Apply. 'loadout revert' undoes it.");
        return 0;
    }

    private static void RefuseUnchoosable(string entry)
    {
        if (entry.StartsWith("param:", StringComparison.Ordinal) || entry.StartsWith("fmg:", StringComparison.Ordinal) || entry.StartsWith("tpf:", StringComparison.Ordinal)) return;
        throw new LoadoutVerbRefusedException(entry.StartsWith("member:", StringComparison.Ordinal)
            ? "Binder members cannot be chosen yet; only param rows, texts and textures can."
            : $"{MarkdownText.Code(entry)} is not a choosable entry. 'fromsoft merge choices list' shows the entries that are.");
    }

    private static List<(ChoiceRow Row, string Source)> Rows(Loadout.ReadOnly loadout, MergeReport report, string? path)
    {
        var stored = LoadoutMergeChoice.FindByLoadout(loadout.Db, loadout.LoadoutId)
            .Where(c => path is null || c.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var rows = new List<(ChoiceRow, string)>();

        foreach (var entry in report.Entries.Where(e => path is null || e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        foreach (var contest in entry.Contests)
        {
            var pin = stored.FirstOrDefault(c => c.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) && c.Key == contest.Key);
            if (pin.IsValid()) stored.Remove(pin);
            var source = contest.Chosen ? "choice"
                : pin.IsValid() ? $"stale ({NameOf(loadout, pin)} no longer edits it)"
                : "priority";
            rows.Add((new ChoiceRow(entry.Path, contest.Key, contest.Candidates, contest.Winner, contest.Chosen, pin.IsValid() ? pin.Id.ToString() : null), source));
        }

        // What is left names an entry no mod contests any more.
        foreach (var pin in stored.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Key, StringComparer.Ordinal))
            rows.Add((new ChoiceRow(pin.Path, pin.Key, [], "", false, pin.Id.ToString()), "stale (nothing contests it)"));
        return rows;
    }

    private static string NameOf(Loadout.ReadOnly loadout, LoadoutMergeChoice.ReadOnly pin)
    {
        var item = LoadoutItem.Load(loadout.Db, LoadoutItemId.From(pin.WinnerGroupId.Value));
        return item.IsValid() ? item.Name : "the chosen mod";
    }
}

using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>What the engine said about a loadout, asked for once so the cards agree with each other.</summary>
public sealed record EngineMergeSnapshot(MergeReport Report, IReadOnlyList<MergedOutput> Outputs, IReadOnlyList<ContestedPath> NotContested);

/// <summary>
/// The merge cards of any game served by an <see cref="IMergeEngine"/>: each change a merge dropped, each path that
/// could not be merged, each path the game loads once per mod, and the merges whose original copy was not trusted.
/// Reads the engine's report and plan, which the apply that follows finds cached.
/// </summary>
public sealed class EngineMergeDiagnosticsEmitter : AMergeDiagnosticsEmitter<EngineMergeSnapshot>
{
    private readonly MergeEngines _engines;
    private readonly Func<Loadout.ReadOnly, AMergeGame> _gameOf;

    public EngineMergeDiagnosticsEmitter(MergeEngines engines, ILogger<EngineMergeDiagnosticsEmitter> logger)
        : this(engines, logger, loadout => ((IHasMergeGame)loadout.InstallationInstance.Game).MergeGame)
    {
    }

    /// <summary>For tests, which have no game class that implements <see cref="IHasMergeGame"/>.</summary>
    internal EngineMergeDiagnosticsEmitter(MergeEngines engines, ILogger<EngineMergeDiagnosticsEmitter> logger, Func<Loadout.ReadOnly, AMergeGame> gameOf)
        : base(logger)
    {
        _engines = engines;
        _gameOf = gameOf;
    }

    protected override EngineMergeSnapshot? TryPlan(Loadout.ReadOnly loadout, out Diagnostic? failure)
    {
        failure = null;
        if (_engines.For(loadout.InstallationInstance.Game) is not { } engine) return null;

        var (report, outputs) = engine.PlanAndReport(loadout, MergeSourceMode.Report);

        // Both come from one plan, so a report entry with no output is a bug in the engine. The losses are listed from
        // the outputs, so nothing is dropped.
        foreach (var entry in report.Entries.Where(e => e.Outcome == MergeReportOutcome.Merged))
        {
            if (!outputs.Any(o => o.RelPath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)))
                Logger.LogError("The merge report lists {Path} as merged but the plan has no output for it", entry.Path);
        }

        return new EngineMergeSnapshot(report, outputs, engine.NotContested(loadout));
    }

    protected override IEnumerable<MergedEntry> Merged(Loadout.ReadOnly loadout, EngineMergeSnapshot plan)
        => plan.Outputs.Select(o => new MergedEntry(o.RelPath, o.Contributors, o.Notes));

    protected override string ReorderPage(Loadout.ReadOnly loadout) => _gameOf(loadout).ReorderHint;

    protected override IEnumerable<DroppedItem> ExtraDropped(Loadout.ReadOnly loadout, EngineMergeSnapshot plan)
    {
        var page = ReorderPage(loadout);
        foreach (var item in NotMergeableItems(plan.Report, page)) yield return item;
        foreach (var item in UntrustedBaseItems(plan)) yield return item;
    }

    // A two-way merge keeps the first mod's copy and every difference in the others is dropped, so "move X below Y"
    // would only swap which mod loses its lines. The base card lists what is dropped instead.
    protected override bool CardsReplaceLossCards(MergedEntry entry) => entry.Notes.Any(n => n is VanillaBaseUntrusted);

    // The game loads every mod's copy, so nothing here is left out: the card says which copy wins where they overlap.
    protected override IEnumerable<Diagnostic> Extra(Loadout.ReadOnly loadout, EngineMergeSnapshot plan)
    {
        var page = ReorderPage(loadout);
        foreach (var path in plan.NotContested)
            yield return MergeDiagnostics.CreateNotContested(path.Rel, string.Join(", ", path.Contributors.Select(c => c.ModName).Distinct()), page);
    }

    // The first contributor's copy is the one the game gets; every other mod's is the losing one.
    private static IEnumerable<DroppedItem> NotMergeableItems(MergeReport report, string page)
    {
        foreach (var entry in report.Entries.Where(e => e.Outcome == MergeReportOutcome.NotMergeable))
        {
            if (entry.Contributors.Count < 2)
            {
                // One mod ships the path from more than one folder, so there is no other mod to name.
                var mod = entry.Contributors.FirstOrDefault() ?? "a mod";
                yield return new DroppedItem(entry.Path, mod, $"{mod} ships it more than once and the files could not be merged ({entry.Reason})", page);
                continue;
            }

            var winner = entry.Contributors[0];
            foreach (var loser in entry.Contributors.Skip(1))
                yield return new DroppedItem($"{loser}'s copy of {entry.Path}", loser, $"{winner}'s copy wins; the files could not be merged ({entry.Reason})", page);
        }
    }

    // Without the game's own copy to compare against, the merge is two-way: the first mod's file is the result and what
    // the other mods changed is left out, including lines they added that nothing else touches. That can break the
    // result (a script that uses a variable the dropped lines declare no longer compiles), so every reason is a Warning.
    // A missing copy raises nothing: a file a mod adds has none, and the Merges page already says so.
    // The advice has nothing to do with load order, so the card has no page.
    private static IEnumerable<DroppedItem> UntrustedBaseItems(EngineMergeSnapshot plan)
    {
        foreach (var output in plan.Outputs)
        {
            if (output.Notes.OfType<VanillaBaseUntrusted>().FirstOrDefault() is not { } untrusted) continue;
            if (output.Contributors.Count < 2) continue;

            var winner = output.Contributors[0];
            var losers = output.Contributors.Skip(1).Distinct().ToArray();
            var both = $"{winner} and {string.Join(" and ", losers)}";
            var dropped = DroppedLines(output, losers);

            var reason = untrusted.Reason switch
            {
                VanillaTrustReasons.StoreFileListNotSavedYet =>
                    $"{untrusted.Reason}, so the app cannot tell the game's lines from the mods' and merged {both} two-way; {dropped} are dropped, " +
                    "which can break the result. Let Steam finish the update (or verify the game's files in Steam), wait for the app's file check, then apply again",
                VanillaTrustReasons.DiffersFromStoreList =>
                    $"the game's copy differs from the store's file list for the installed build (another tool or an earlier merge changed it), so {both} were merged two-way and {dropped} are dropped. " +
                    "Verify the game's files in Steam to restore the original, then apply again",
                VanillaTrustReasons.NotInstalledByStore =>
                    $"the store did not install this file, so there is no original to merge against; {both} were merged two-way and {dropped} are dropped. " +
                    "If a mod's content is missing in game, keep only one of the mods that ship this file",
                _ =>
                    $"{untrusted.Reason}, so {both} were merged two-way and {dropped} are dropped. " +
                    "If a mod's content is missing in game, keep only one of the mods that ship this file",
            };

            yield return new DroppedItem($"the game's own copy of {untrusted.Path}", "the game folder", reason, null);
        }
    }

    // "Beta's lines 12–13, 40–40 and Gamma's lines 3–3"; a merge that notes no lines just says the mod's changes.
    private static string DroppedLines(MergedOutput output, string[] losers)
    {
        var parts = new List<string>();
        foreach (var loser in losers)
        {
            var ranges = output.Notes.OfType<LinesKept>().Where(k => k.DroppedMod == loser).OrderBy(k => k.FirstLine)
                .Select(k => $"{k.FirstLine}–{k.LastLine}").ToArray();
            parts.Add(ranges.Length == 0 ? $"{loser}'s own changes" : $"{loser}'s lines {string.Join(", ", ranges)}");
        }

        return string.Join(" and ", parts);
    }
}

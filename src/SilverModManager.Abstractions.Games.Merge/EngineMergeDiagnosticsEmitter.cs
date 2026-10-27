using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>
/// The merge cards of any game served by an <see cref="IMergeEngine"/>: each change a merge dropped, each path that
/// could not be merged, each path the game loads once per mod, and each merge whose original copy was missing or not
/// trusted. Reads the engine's report and plan, which the apply that follows finds cached.
/// </summary>
public sealed class EngineMergeDiagnosticsEmitter : AMergeDiagnosticsEmitter<MergeReport>
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

    private IMergeEngine? EngineFor(Loadout.ReadOnly loadout) => _engines.For(loadout.InstallationInstance.Game);

    protected override MergeReport? TryPlan(Loadout.ReadOnly loadout, out Diagnostic? failure)
    {
        failure = null;
        return EngineFor(loadout)?.Report(loadout, MergeSourceMode.Report);
    }

    protected override IEnumerable<MergedEntry> Merged(Loadout.ReadOnly loadout, MergeReport plan)
    {
        // The report holds sentences; the notes with their losses are in the plan, which the report just made.
        var outputs = EngineFor(loadout)!.Plan(loadout, MergeSourceMode.Report)
            .ToDictionary(o => o.RelPath, o => o, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan.Entries.Where(e => e.Outcome == MergeReportOutcome.Merged))
        {
            if (!outputs.TryGetValue(entry.Path, out var output)) continue;
            yield return new MergedEntry(entry.Path, string.Join(", ", entry.Contributors), output.Notes);
        }
    }

    protected override IEnumerable<DroppedItem> ExtraDropped(Loadout.ReadOnly loadout, MergeReport plan)
    {
        var page = ReorderPage(loadout);
        return VanillaBaseItems(loadout, plan, page).Concat(NotMergeableItems(plan, page)).Concat(NotContestedItems(loadout, page));
    }

    // Every loser of a path that could not be merged; the first contributor's copy is the one the game gets.
    private static IEnumerable<DroppedItem> NotMergeableItems(MergeReport plan, string page)
    {
        foreach (var entry in plan.Entries.Where(e => e.Outcome == MergeReportOutcome.NotMergeable))
        {
            if (entry.Contributors.Count == 0) continue;
            var winner = entry.Contributors[0];
            foreach (var loser in entry.Contributors.Skip(1))
                yield return new DroppedItem($"{loser}'s copy of {entry.Path}", loser, $"{winner}'s copy wins; the files could not be merged", page);
        }
    }

    // A game that loads every mod's copy has no unused copy, so these are a Suggestion about which copy wins where they overlap.
    private IEnumerable<DroppedItem> NotContestedItems(Loadout.ReadOnly loadout, string page)
    {
        foreach (var path in EngineFor(loadout)!.NotContested(loadout))
        {
            var mods = string.Join(", ", path.Contributors.Select(c => c.ModName).Distinct());
            yield return new DroppedItem(
                path.Rel, mods,
                $"it ships in {mods}: bundles are not merged, so where they contain the same file the higher-priority mod's copy is used",
                page, DiagnosticSeverity.Suggestion);
        }
    }

    // Without the game's own copy to compare against, the mods are merged two-way and the higher-ranked one wins every difference.
    private IEnumerable<DroppedItem> VanillaBaseItems(Loadout.ReadOnly loadout, MergeReport plan, string page)
    {
        foreach (var entry in Merged(loadout, plan))
        {
            foreach (var note in entry.Notes)
            {
                switch (note)
                {
                    case NoVanillaBase none:
                        yield return new DroppedItem(
                            $"the game's own copy of {none.Path}", "the game folder",
                            $"there is no copy to compare the mods against, so they were merged two-way and {Winner(entry)}'s version wins where they differ", page, DiagnosticSeverity.Suggestion);
                        break;
                    case VanillaBaseUntrusted untrusted:
                        yield return new DroppedItem(
                            $"the game's own copy of {untrusted.Path}", "the game folder",
                            $"{untrusted.Reason}, so the mods were merged two-way and {Winner(entry)}'s version wins where they differ", page, DiagnosticSeverity.Suggestion);
                        break;
                }
            }
        }
    }

    private static string Winner(MergedEntry entry) => entry.Mods.Split(", ")[0];

    protected override string ReorderAdvice(Loadout.ReadOnly loadout, string dropped, string kept) => _gameOf(loadout).ReorderAdvice(dropped, kept);
}

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
    private const int MaxNamedPaths = 5;

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

    protected override string ReorderAdvice(Loadout.ReadOnly loadout, string dropped, string kept) => _gameOf(loadout).ReorderAdvice(dropped, kept);

    protected override IEnumerable<DroppedItem> ExtraDropped(Loadout.ReadOnly loadout, EngineMergeSnapshot plan)
    {
        var page = ReorderPage(loadout);
        foreach (var item in NotMergeableItems(plan.Report, page)) yield return item;
        if (UntrustedBaseItem(plan, page) is { } untrusted) yield return untrusted;
    }

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

    // The game's own copy was changed by another tool, so the mods are merged two-way and the higher-ranked one wins every
    // difference. A missing copy raises nothing: a file a mod adds has none, and the Merges page already says so.
    private static DroppedItem? UntrustedBaseItem(EngineMergeSnapshot plan, string page)
    {
        var untrusted = plan.Outputs.SelectMany(o => o.Notes).OfType<VanillaBaseUntrusted>().DistinctBy(n => n.Path).ToArray();
        if (untrusted.Length == 0) return null;

        var paths = string.Join(", ", untrusted.Take(MaxNamedPaths).Select(n => n.Path));
        if (untrusted.Length > MaxNamedPaths) paths += $" and {untrusted.Length - MaxNamedPaths} more";
        var reasons = string.Join("; ", untrusted.Select(n => n.Reason).Distinct());
        return new DroppedItem(
            $"the game's own copy of {paths}", "the game folder",
            $"{reasons}, so the mods were merged without it and where they differ the higher-priority mod's version wins", page, DiagnosticSeverity.Suggestion);
    }
}

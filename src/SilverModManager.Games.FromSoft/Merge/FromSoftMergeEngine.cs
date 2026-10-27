using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>The FromSoft fold behind the shared engine interface; it adds nothing of its own to the merge.</summary>
public sealed class FromSoftMergeEngine(MergeReports reports, MergePlanCache plans, LoadoutMergeSources sources) : IMergeEngine
{
    public bool Serves(IGameData game) => game is IFromSoftGame;

    // FromSoft's report is always built in report mode.
    public MergeReport Report(Loadout.ReadOnly loadout, MergeSourceMode mode) => reports.Report(loadout);

    public IReadOnlyList<MergedOutput> Plan(Loadout.ReadOnly loadout, MergeSourceMode mode)
        => plans.Current(loadout, mode).Merged
            .Select(m => new MergedOutput(Me3Path(m.Path), m.Path, m.Bytes, m.Contributors.Select(c => c.ModName).Distinct().ToArray(), m.Notes, MergeStrategies.Name(m.Strategy)))
            .ToArray();

    public IReadOnlyList<PlannedOutput> PlannedOutputs(Loadout.ReadOnly loadout)
        => sources.Index(loadout).Candidates
            .Where(c => c.Outcome.Merges && !plans.UnmergedPaths(loadout.LoadoutId).Contains(c.Path))
            .Select(c => new PlannedOutput(Me3Path(c.Path), c.Path))
            .ToArray();

    public byte[]? MergedBytes(Loadout.ReadOnly loadout, string relPath) => plans.MergedBytes(loadout, relPath);

    private static GamePath Me3Path(string rel) => new(LocationId.Me3Mods, $"{Me3ModsLayout.MergedPackageId}/{rel}");
}

using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>One merged file the next apply writes. <paramref name="Contributors"/> are mod names, highest priority first.</summary>
public sealed record MergedOutput(
    GamePath Output, string RelPath, byte[] Bytes, IReadOnlyList<string> Contributors, IReadOnlyList<IMergeNote> Notes, string Strategy);

/// <summary>The report and the outputs of one plan, for a caller that shows both and must not see two loadout states.</summary>
public sealed record MergePlanReport(MergeReport Report, IReadOnlyList<MergedOutput> Outputs);

/// <summary>Where one merged file will be written, known without reading any file bytes.</summary>
public readonly record struct PlannedOutput(GamePath Output, string RelPath);

/// <summary>
/// What the UI, the CLI and the synchronizer ask about a game's merges. One engine serves one game;
/// <see cref="MergeEngines"/> picks it.
/// </summary>
public interface IMergeEngine : IMergePlanSource
{
    /// <summary>Whether this engine merges <paramref name="game"/>'s files.</summary>
    bool Serves(IGameData game);

    /// <summary>What the apply would do for <paramref name="loadout"/>, file by file.</summary>
    MergeReport Report(Loadout.ReadOnly loadout, MergeSourceMode mode);

    /// <summary>The files the next apply writes, with their bytes.</summary>
    IReadOnlyList<MergedOutput> Plan(Loadout.ReadOnly loadout, MergeSourceMode mode);

    /// <summary>
    /// <see cref="Report"/> and <see cref="Plan"/> made from one plan. Two separate calls each look at the loadout
    /// again, and a change in between gives a report that lists merges the plan has no output for.
    /// </summary>
    MergePlanReport PlanAndReport(Loadout.ReadOnly loadout, MergeSourceMode mode);

    /// <summary>
    /// The paths the next <c>Plan(Apply)</c> writes: every contested path with a strategy, minus the paths whose latest
    /// plan failed (empty until a plan has been made, as <c>MergePlanCache.UnmergedPaths</c> is). Reads loadout rows
    /// only, never file bytes: the synchronizer calls it on every refresh inside the winning-files query.
    /// </summary>
    IReadOnlyList<PlannedOutput> PlannedOutputs(Loadout.ReadOnly loadout);

    /// <summary>
    /// The paths several mod folders ship that the game loads per mod folder, so nothing is merged or reported for
    /// them (<see cref="AMergeGame.Contests"/> false). Contributors are ranked, so the first is the copy that wins where
    /// the game picks one and a Health Check card can name the others. Empty for an engine with no such notion.
    /// </summary>
    IReadOnlyList<ContestedPath> NotContested(Loadout.ReadOnly loadout);
}

using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>
/// An apply whose merged file is not in the sync tree. Deploying the contributors whole instead would leave the game
/// to pick one of them. The message is shown to the player as is.
/// </summary>
public class MergeOutputMissingException(string relPath) : SynchronizerPreflightException(
    $"{relPath} is provided by several mods but its merged file is not part of this apply; deploying the copies whole would leave it to load order which one wins");

/// <summary>
/// What a game's synchronizer composes so that the merge engine's outputs are deployed as merged files and the
/// copies they stand in for are not. The synchronizer calls <see cref="PlanOutputs"/> from its loadout preflight,
/// before the sync tree is built, so <see cref="IntrinsicFiles"/> already knows which merges failed: a path that
/// could not be merged is left out and deploys by load order like any other conflict. Nothing here refuses an apply
/// for a merge problem; it refuses only a tree that lacks a file the plan will write.
/// </summary>
public sealed class MergeSynchronizerPart(IMergeEngine engine)
{
    /// <summary>
    /// One <see cref="MergedIntrinsicFile"/> per path the engine will write, keyed by its output path. This runs on
    /// every refresh, inside the winning-files query, so it reads loadout rows and no file bytes and never plans.
    /// It never throws: a failure is logged and nothing is listed, and <see cref="CheckOutputs"/> then refuses
    /// the apply if the plan has outputs the tree lacks.
    /// </summary>
    public Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout, ILogger logger)
    {
        var files = new Dictionary<GamePath, IIntrinsicFile>();
        try
        {
            foreach (var planned in engine.PlannedOutputs(loadout))
                files[planned.Output] = new MergedIntrinsicFile(engine, planned.Output, planned.RelPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The merged files of the loadout could not be listed, so none are deployed until they can be");
            files.Clear();
        }
        return files;
    }

    /// <summary>
    /// Plans in Apply mode, the one mode that reads file bytes. Called from the synchronizer's loadout preflight so
    /// the engine has recorded which merges failed before the listing is asked for.
    /// </summary>
    public IReadOnlyList<MergedOutput> PlanOutputs(Loadout.ReadOnly loadout) => engine.Plan(loadout, MergeSourceMode.Apply);

    /// <summary>
    /// Throws <see cref="MergeOutputMissingException"/> for the first planned output that is not an
    /// <see cref="LoadoutSourceItemType.Intrinsic"/> node in <paramref name="tree"/>. Outputs of a merge that failed are
    /// not in the plan, so they are not asked for.
    /// </summary>
    public void CheckOutputs(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly loadout)
    {
        foreach (var output in PlanOutputs(loadout))
        {
            if (!tree.TryGetValue(output.Output, out var node) || node.SourceItemType != LoadoutSourceItemType.Intrinsic)
                throw new MergeOutputMissingException(output.RelPath);
        }
    }
}

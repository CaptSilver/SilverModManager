using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>
/// An apply whose merged file is not in the sync tree. Deploying the contributors whole instead would leave the game
/// to pick one of them. The message is shown to the player as is.
/// </summary>
public sealed class MergeOutputMissingException(string relPath) : SynchronizerPreflightException(
    $"{relPath} is provided by several mods but its merged file is not part of this apply; deploying the copies whole would leave it to load order which one wins");

/// <summary>An apply the merge part stops before anything is written, for a reason that is not one missing file. The message is shown as is.</summary>
public sealed class MergeApplyRefusedException(string reason) : SynchronizerPreflightException(reason)
{
    /// <summary>The loadout, or the merge plan made from it, changed between the preflight and the sync tree.</summary>
    public static MergeApplyRefusedException Changed() => new("The loadout changed while it was being applied. Apply again.");

    public static MergeApplyRefusedException PlanFailed(Exception cause) => new($"The files the mods provide could not be merged: {cause.Message}");

    public static MergeApplyRefusedException ListingFailed(string reason) => new($"The files the mods provide could not be listed: {reason}");
}

/// <summary>
/// What a game's synchronizer composes so that the merge engine's outputs are deployed as merged files. The
/// synchronizer calls <see cref="PlanOutputs"/> from its loadout preflight, before the sync tree is built, so
/// <see cref="IntrinsicFiles"/> already knows which merges failed: a path that could not be merged is left out and
/// deploys by load order like any other conflict. It may call <see cref="PlanOutputs"/> again from
/// <c>PrepareLoadoutForSync</c> to shrink the window in which the plan can move under the tree. The contributors'
/// own copies are not touched here; hiding them is the synchronizer's <c>AdjustActions</c> job if its game needs it.
/// <para>
/// <see cref="CheckOutputs"/> belongs in <c>PreflightActions</c>. Nothing here refuses an apply because a merge
/// failed. It refuses a tree that disagrees with the plan, in either direction: a planned file the tree lacks would
/// deploy its contributors whole, and a listed file the plan lacks would crash the write half way through.
/// </para>
/// </summary>
public sealed class MergeSynchronizerPart(IMergeEngine engine)
{
    // Only the latest revision of each loadout is kept; an older one is never asked for again.
    private readonly ConcurrentDictionary<LoadoutId, (TxId Revision, IReadOnlyList<PlannedOutput> Outputs, Dictionary<GamePath, IIntrinsicFile> Files)> _listed = new();
    private readonly ConcurrentDictionary<LoadoutId, (TxId Revision, string Reason)> _listingFailures = new();

    /// <summary>
    /// One <see cref="MergedIntrinsicFile"/> per path the engine will write, keyed by its output path. This runs on
    /// every refresh, inside the winning-files query, so it reads loadout rows and no file bytes and never plans.
    /// It never throws: a failure is logged and nothing is listed, and <see cref="CheckOutputs"/> refuses the apply
    /// of that revision with the reason.
    /// </summary>
    public Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout, ILogger logger)
    {
        var revision = loadout.Db.BasisTxId;
        try
        {
            var planned = engine.PlannedOutputs(loadout);
            if (_listed.TryGetValue(loadout.LoadoutId, out var known) && known.Revision == revision && known.Outputs.SequenceEqual(planned))
                return known.Files;

            var files = new Dictionary<GamePath, IIntrinsicFile>();
            foreach (var output in planned)
                files[output.Output] = new MergedIntrinsicFile(engine, output.Output, output.RelPath);
            _listed[loadout.LoadoutId] = (revision, planned, files);
            return files;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The merged files of {Loadout} could not be listed, so applying it is refused until they can be", loadout.Name);
            _listingFailures[loadout.LoadoutId] = (revision, exception.Message);
            _listed.TryRemove(loadout.LoadoutId, out _);
            return [];
        }
    }

    /// <summary>
    /// Plans in Apply mode, the one mode that reads file bytes. Called from the synchronizer's loadout preflight so
    /// the engine has recorded which merges failed before the listing is asked for. A strategy's refusal of its input
    /// surfaces as a readable preflight exception.
    /// </summary>
    public IReadOnlyList<MergedOutput> PlanOutputs(Loadout.ReadOnly loadout)
    {
        try
        {
            return engine.Plan(loadout, MergeSourceMode.Apply);
        }
        catch (MergeInputException exception)
        {
            throw MergeApplyRefusedException.PlanFailed(exception);
        }
    }

    /// <summary>
    /// Compares the tree with the plan. Throws <see cref="MergeOutputMissingException"/> for the first planned output
    /// that is not an <see cref="LoadoutSourceItemType.Intrinsic"/> node in <paramref name="tree"/>, and
    /// <see cref="MergeApplyRefusedException"/> when the revision's listing failed or the tree holds a merged file
    /// the plan no longer has (the plan moved after the listing). Outputs of a merge that failed are not in the
    /// plan, so they are not asked for.
    /// </summary>
    public void CheckOutputs(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly loadout)
    {
        if (_listingFailures.TryGetValue(loadout.LoadoutId, out var failed) && failed.Revision == loadout.Db.BasisTxId)
            throw MergeApplyRefusedException.ListingFailed(failed.Reason);

        var plan = PlanOutputs(loadout);
        var listed = _listed.TryGetValue(loadout.LoadoutId, out var known) ? known.Files : null;

        foreach (var output in plan)
        {
            if (tree.TryGetValue(output.Output, out var node) && node.SourceItemType == LoadoutSourceItemType.Intrinsic) continue;
            throw listed is not null && !listed.ContainsKey(output.Output)
                ? MergeApplyRefusedException.Changed()
                : new MergeOutputMissingException(output.RelPath);
        }

        if (listed is null) return;
        var planned = plan.Select(o => o.Output).ToHashSet();
        foreach (var path in listed.Keys)
        {
            if (!planned.Contains(path) && tree.TryGetValue(path, out var node) && node.SourceItemType == LoadoutSourceItemType.Intrinsic)
                throw MergeApplyRefusedException.Changed();
        }
    }
}

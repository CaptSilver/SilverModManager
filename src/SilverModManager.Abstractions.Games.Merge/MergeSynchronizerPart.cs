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

    /// <summary>A merged file is written for a revision whose plan no check verified, so there are no checked bytes to write.</summary>
    public static MergeApplyRefusedException Unchecked(string relPath)
        => new($"{relPath} is merged from several mods, but the merge was not checked before this apply wrote it, so it is not written. Apply again.");

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
/// <para>
/// The merged files it lists read their bytes from here, and the bytes are the plan <see cref="CheckOutputs"/>
/// verified for that revision; a write for a revision no check passed for is refused. Asking the engine again from each write would scan the loadout and read every
/// contested original once per file, and could hand a write bytes that were never checked.
/// </para>
/// </summary>
public sealed class MergeSynchronizerPart(IMergeEngine engine, ILogger logger) : IMergePlanSource
{
    // The latest revision of each loadout plus the one most recently listed, which differs when something lists an
    // older revision (a reset renders the last synced one). CheckOutputs reads the entry of the revision it checks.
    private readonly ConcurrentDictionary<(LoadoutId Loadout, TxId Revision), (IReadOnlyList<PlannedOutput> Outputs, Dictionary<GamePath, IIntrinsicFile> Files)> _listed = new();
    private readonly ConcurrentDictionary<LoadoutId, (TxId Revision, string Reason)> _listingFailures = new();

    // The plan the latest passing CheckOutputs of each loadout verified, by relative path. Only the next passing check
    // replaces it, never a listing: a refresh that lands while an apply writes must not take the bytes from under it.
    private readonly ConcurrentDictionary<LoadoutId, (TxId Revision, IReadOnlyDictionary<string, MergedOutput> Outputs)> _checked = new();

    /// <summary>
    /// One <see cref="MergedIntrinsicFile"/> per path the engine will write, keyed by its output path. This runs on
    /// every refresh, inside the winning-files query, so it reads loadout rows and no file bytes and never plans.
    /// It never throws: a failure is logged and nothing is listed, and <see cref="CheckOutputs"/> refuses the apply
    /// of that revision with the reason. The result is the caller's to add to.
    /// </summary>
    public Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout, ILogger logger)
    {
        var revision = loadout.Db.BasisTxId;
        try
        {
            var planned = engine.PlannedOutputs(loadout);
            if (_listingFailures.TryGetValue(loadout.LoadoutId, out var failed) && failed.Revision == revision)
                _listingFailures.TryRemove(loadout.LoadoutId, out _);
            if (_listed.TryGetValue((loadout.LoadoutId, revision), out var known) && known.Outputs.SequenceEqual(planned))
                return new Dictionary<GamePath, IIntrinsicFile>(known.Files);

            var files = new Dictionary<GamePath, IIntrinsicFile>();
            foreach (var output in planned)
                files[output.Output] = new MergedIntrinsicFile(this, output.Output, output.RelPath);
            _listed[(loadout.LoadoutId, revision)] = (planned, files);
            foreach (var key in _listed.Keys.Where(k => k.Loadout == loadout.LoadoutId && k.Revision < revision).ToArray())
                _listed.TryRemove(key, out _);
            return new Dictionary<GamePath, IIntrinsicFile>(files);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The merged files of {Loadout} could not be listed, so applying it is refused until they can be", loadout.Name);
            _listingFailures[loadout.LoadoutId] = (revision, exception.Message);
            _listed.TryRemove((loadout.LoadoutId, revision), out _);
            return [];
        }
    }

    /// <summary>
    /// Plans in Apply mode, the one mode that reads file bytes. Called from the synchronizer's loadout preflight so
    /// the engine has recorded which merges failed before the listing is asked for. Anything planning throws becomes a
    /// readable preflight refusal. The real <see cref="MergeEngine"/> catches a strategy's refusal of its input per
    /// path and reports the path as not mergeable, so what reaches here from it is a bug in a strategy, which is
    /// logged with its stack.
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
        catch (Exception exception) when (exception is not (OperationCanceledException or SynchronizerPreflightException))
        {
            logger.LogError(exception, "Planning the merges of {Loadout} failed", loadout.Name);
            throw MergeApplyRefusedException.PlanFailed(exception);
        }
    }

    /// <summary>
    /// Compares the tree with the plan. Throws <see cref="MergeOutputMissingException"/> for the first planned output
    /// that is not an <see cref="LoadoutSourceItemType.Intrinsic"/> node in <paramref name="tree"/>, and
    /// <see cref="MergeApplyRefusedException"/> when the revision's listing failed or the tree holds a merged file
    /// the plan no longer has, or no listing was made for this revision (the plan moved after the listing). Outputs of
    /// a merge that failed are not in the plan, so they are not asked for.
    /// </summary>
    public void CheckOutputs(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly loadout)
    {
        ThrowIfListingFailed(loadout);

        var plan = PlanOutputs(loadout);
        // The tree is built from the listing of this revision. A listing of a newer revision evicts it, so a refresh
        // that ran while the apply held this revision leaves none; list it again (reading loadout rows only) rather
        // than refuse. If the plan has since moved, the two cannot be compared and the write could meet a file the
        // plan lacks.
        if (!_listed.ContainsKey((loadout.LoadoutId, loadout.Db.BasisTxId)))
        {
            IntrinsicFiles(loadout, logger);
            ThrowIfListingFailed(loadout);
        }
        if (!_listed.TryGetValue((loadout.LoadoutId, loadout.Db.BasisTxId), out var known)) throw MergeApplyRefusedException.Changed();
        var listed = known.Files;

        foreach (var output in plan)
        {
            if (tree.TryGetValue(output.Output, out var node) && node.SourceItemType == LoadoutSourceItemType.Intrinsic) continue;
            throw !listed.ContainsKey(output.Output)
                ? MergeApplyRefusedException.Changed()
                : new MergeOutputMissingException(output.RelPath);
        }

        var planned = plan.Select(o => o.Output).ToHashSet();
        foreach (var path in listed.Keys)
        {
            if (!planned.Contains(path) && tree.TryGetValue(path, out var node) && node.SourceItemType == LoadoutSourceItemType.Intrinsic)
                throw MergeApplyRefusedException.Changed();
        }

        _checked[loadout.LoadoutId] = (loadout.Db.BasisTxId, plan.ToDictionary(o => o.RelPath, StringComparer.OrdinalIgnoreCase));
    }

    private void ThrowIfListingFailed(Loadout.ReadOnly loadout)
    {
        if (_listingFailures.TryGetValue(loadout.LoadoutId, out var failed) && failed.Revision == loadout.Db.BasisTxId)
            throw MergeApplyRefusedException.ListingFailed(failed.Reason);
    }

    /// <summary>
    /// The bytes <see cref="CheckOutputs"/> verified for <paramref name="relPath"/> at this revision, or null when that
    /// plan has no merge for it. Throws <see cref="MergeApplyRefusedException"/> for a revision no check passed for:
    /// asking the engine instead would write bytes nothing compared with the tree.
    /// </summary>
    public byte[]? MergedBytes(Loadout.ReadOnly loadout, string relPath)
    {
        if (_checked.TryGetValue(loadout.LoadoutId, out var verified) && verified.Revision == loadout.Db.BasisTxId)
            return verified.Outputs.TryGetValue(relPath, out var output) ? output.Bytes : null;
        throw MergeApplyRefusedException.Unchecked(relPath);
    }
}

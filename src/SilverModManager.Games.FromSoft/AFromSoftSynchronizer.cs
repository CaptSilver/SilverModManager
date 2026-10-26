using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft;

/// <summary>
/// Keeps the game's subfolder out of every code path that writes: mods live in a package folder beside the app's
/// data and reach the game through the launcher, never through a deployment.
/// <para>
/// Files that several mods provide and the engine can merge are written once, into the merged package, and the mods'
/// own copies stay off disk. If the merge can't be made, the apply is refused before anything is written: deploying
/// the copies whole would leave it to me3 which one wins. A file with no merge strategy is not a merge that failed:
/// its copies deploy whole and the profile's package order, which follows loadout priority, decides the winner.
/// </para>
/// </summary>
public abstract class AFromSoftSynchronizer : ALoadoutSynchronizer
{
    private readonly FromSoftGameLayout _layout;
    private readonly Dictionary<GamePath, IIntrinsicFile> _intrinsicFiles;
    private readonly Lazy<LoadoutMergeSources> _sources;
    private readonly Lazy<MergePlanCache> _plans;

    // Only the latest revision of each loadout is kept: the listing is asked for on every refresh and an older one is never asked for again.
    private readonly ConcurrentDictionary<LoadoutId, (TxId Revision, Dictionary<GamePath, IIntrinsicFile> Files)> _listed = new();
    private readonly ConcurrentDictionary<LoadoutId, (TxId Revision, string Reason)> _listingFailures = new();

    protected AFromSoftSynchronizer(IServiceProvider provider, IFromSoftGame game, IEnumerable<IIntrinsicFile> intrinsicFiles) : base(provider)
    {
        _layout = game.Layout;
        _intrinsicFiles = intrinsicFiles.ToDictionary(file => file.Path);
        _sources = new Lazy<LoadoutMergeSources>(provider.GetRequiredService<LoadoutMergeSources>);
        _plans = new Lazy<MergePlanCache>(provider.GetRequiredService<MergePlanCache>);
        GameSpecificPathFilter = GamePathFilters.Create(path => FromSoftPaths.IsArchiveOrMedia(_layout, path));
    }

    /// <summary>
    /// The loader's files plus a merged file for every path the engine will merge. This runs on every refresh, inside
    /// the winning-files query, so it reads the loadout's items and the library's entries and no file bytes. It never
    /// throws: a failure is recorded for this revision and the loader's files alone are returned, and the apply of
    /// that revision is refused by <see cref="PreflightLoadout"/>.
    /// </summary>
    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout)
    {
        var revision = loadout.Db.BasisTxId;
        if (_listed.TryGetValue(loadout.LoadoutId, out var known) && known.Revision == revision) return known.Files;

        var files = new Dictionary<GamePath, IIntrinsicFile>(_intrinsicFiles);
        try
        {
            foreach (var candidate in _sources.Value.Index(loadout).Candidates.Where(c => c.Outcome.Merges))
            {
                var merged = new MergedFile(_plans.Value, candidate.Path);
                files[merged.Path] = merged;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            files = new Dictionary<GamePath, IIntrinsicFile>(_intrinsicFiles);
            Logger.LogError(exception, "The files that mods provide in {Loadout} could not be listed, so applying it is refused until they can be", loadout.Name);
            _listingFailures[loadout.LoadoutId] = (revision, $"the files the mods provide could not be listed: {exception.Message}");
        }

        _listed[loadout.LoadoutId] = (revision, files);
        return files;
    }

    protected override IGamePathFilter GameSpecificPathFilter { get; }

    public override bool IsIgnoredBackupPath(GamePath path) => FromSoftPaths.IsInGameFolder(_layout, path);

    protected override bool IsNeverWritten(GamePath path) => FromSoftPaths.IsInGameFolder(_layout, path);

    /// <summary>
    /// A merged file stands in for every mod's copy of its path, so the copies are taken off disk: one already
    /// deployed is deleted, one the player edited is kept in the file store first and is never read back as the mod's
    /// new content, and one that was never deployed stays that way. Actions are assigned as a whole because the
    /// mapping would otherwise leave an ingest on an edited copy, and a node with nothing to do must read as
    /// <see cref="Actions.DoNothing"/> or the loadout looks out of sync forever.
    /// </summary>
    protected override void AdjustActions(Dictionary<GamePath, SyncNode> tree)
    {
        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, node) in tree)
        {
            if (node.SourceItemType != LoadoutSourceItemType.Intrinsic || path.LocationId != LocationId.Me3Mods) continue;
            if (SplitPackage(path) is { Package: Me3ModsLayout.MergedPackageId } split) merged.Add(split.Rel);
        }
        if (merged.Count == 0) return;

        foreach (var path in tree.Keys.ToArray())
        {
            if (path.LocationId != LocationId.Me3Mods) continue;
            if (SplitPackage(path) is not { } split || split.Package.Equals(Me3ModsLayout.MergedPackageId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!merged.Contains(split.Rel)) continue;

            ref var node = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(tree, path);
            if (node.SourceItemType != LoadoutSourceItemType.Loadout) continue;

            node.Actions = !node.HaveDisk
                ? Actions.DoNothing
                : node.Disk.Hash != node.Loadout.Hash
                    ? Actions.BackupFile | Actions.DeleteFromDisk
                    : Actions.DeleteFromDisk;
        }
    }

    private static (string Package, string Rel)? SplitPackage(GamePath path)
    {
        var parts = path.Path.Path.Split('/', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    /// <summary>
    /// Plans the merges without writing, so a merge that can't be made refuses the apply before a switch deactivates
    /// the loadout it replaces. A byte-level refusal such as a regulation whose build has no vanilla to compare against surfaces here too.
    /// </summary>
    protected override Task PreflightLoadout(Loadout.ReadOnly loadout)
    {
        if (_listingFailures.TryGetValue(loadout.LoadoutId, out var failed) && failed.Revision == loadout.Db.BasisTxId)
            throw new MergeRefusedException(failed.Reason);

        RefuseIfNotMergeable(Plan(loadout, MergeSourceMode.Report));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Plans once more, now allowed to keep a vanilla regulation, and checks the tree has a merged file for every
    /// merge. Without one the contributors would deploy whole, which is what an earlier listing failure leaves behind.
    /// </summary>
    protected override Task PreflightActions(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly? loadout)
    {
        if (loadout is not { } current) return Task.CompletedTask;

        var plan = Plan(current, MergeSourceMode.Apply);
        RefuseIfNotMergeable(plan);
        foreach (var merged in plan.Merged)
        {
            var path = new GamePath(LocationId.Me3Mods, $"{Me3ModsLayout.MergedPackageId}/{merged.Path}");
            if (!tree.TryGetValue(path, out var node) || node.SourceItemType != LoadoutSourceItemType.Intrinsic)
                throw new MergeRefusedException($"{merged.Path} is provided by several mods but its merged file is not part of this apply, and deploying the copies whole would leave it to me3 which one wins");
        }
        return Task.CompletedTask;
    }

    private MergePlan Plan(Loadout.ReadOnly loadout, MergeSourceMode mode)
    {
        try
        {
            return _plans.Value.Current(loadout, mode);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or MergeRefusedException))
        {
            Logger.LogError(exception, "Planning the merges of {Loadout} failed", loadout.Name);
            throw MergeRefusedException.PlanFailed(exception);
        }
    }

    private static void RefuseIfNotMergeable(MergePlan plan)
    {
        if (plan.Refusal is { } refusal) throw new MergeRefusedException(refusal);
    }
}

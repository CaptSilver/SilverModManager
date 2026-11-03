using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Loadouts;

/// <summary>
/// Deletes file rows from a loadout. A removal is two transactions: a restore point first, so reverting to it
/// brings the rows back, then one transaction that deletes them. The groups stay; only files leave them.
/// </summary>
internal sealed class LoadoutFileRemover(
    IConnection connection,
    ICollectionOwnership ownership,
    ILogger<LoadoutFileRemover> logger) : ILoadoutFileRemover
{
    private const string ExternalChangesName = "External Changes";

    /// <summary>For tests that need the removal commit to fail after the restore point is written.</summary>
    internal Action<ITransaction>? BeforeRemovalCommit { get; init; }

    public FileRemovalPlan Plan(FileRemovalRequest request)
    {
        var db = connection.Db;
        var loadout = Loadout.Load(db, request.LoadoutId);
        if (!loadout.IsValid())
            return new FileRemovalPlan(request, []);

        var winners = ALoadoutSynchronizer.WinningRowIds(loadout);
        var rows = new Dictionary<EntityId, FileRemovalRow>();
        foreach (var item in FindRows(db, request))
        {
            if (rows.ContainsKey(item.Id)) continue;
            if (ToRow(db, item, winners, request.OnlyInGroups is not null) is { } row)
                rows.Add(item.Id, row);
        }

        return new FileRemovalPlan(request, rows.Values.OrderBy(r => r.Path.Path.ToString(), StringComparer.Ordinal).ThenBy(r => r.RowId.Value).ToArray());
    }

    public async Task<FileRemovalResult> Remove(FileRemovalRequest request, bool includeLocked)
    {
        if (!Loadout.Load(connection.Db, request.LoadoutId).IsValid())
            throw new FileRemovalRefusedException("The loadout no longer exists.");

        // The caller's earlier plan was for its dialog; the database may have moved since.
        var plan = Plan(request);
        var toRemove = (includeLocked ? plan.Removable.Concat(plan.Locked) : plan.Removable).ToArray();
        if (toRemove.Length == 0)
            throw new FileRemovalRefusedException(RefusalMessage(plan));

        // The marker has to precede the removal: reverting restores the state as of the marked transaction.
        using var mark = connection.BeginTransaction();
        LoadoutSnapshot.Mark(mark, request.LoadoutId);
        var restorePoint = (await mark.Commit()).NewTx;

        using var tx = connection.BeginTransaction();
        foreach (var row in toRemove)
            tx.Delete(row.RowId, recursive: false);
        BeforeRemovalCommit?.Invoke(tx);
        await tx.Commit();

        var removed = toRemove.Select(r => r.Path).Distinct().ToArray();
        logger.LogInformation("Removed {RowCount} file rows at {PathCount} paths from loadout {Loadout}", toRemove.Length, removed.Length, request.LoadoutId);
        return new FileRemovalResult(removed, restorePoint);
    }

    private static string RefusalMessage(FileRemovalPlan plan)
    {
        var lines = new List<string>();
        lines.AddRange(plan.Blocked.Select(r => r.Blocker).OfType<string>().Distinct());
        lines.AddRange(plan.Locked.Select(r => $"`{r.Path.Path}` belongs to {r.CollectionName}, which stays as it is unless locked files are included.").Distinct());
        if (lines.Count == 0)
            lines.AddRange(plan.Request.Paths.Distinct().Select(p => $"Nothing in the loadout provides `{p.Path}`."));
        return string.Join('\n', lines);
    }

    private static IEnumerable<LoadoutItemWithTargetPath.ReadOnly> FindRows(IDb db, FileRemovalRequest request)
    {
        var paths = request.Paths.Distinct().ToArray();

        if (request.OnlyInGroups is { } groupIds)
        {
            var groups = groupIds.Select(id => LoadoutItemGroup.Load(db, id)).Where(g => g.IsValid()).ToArray();
            return groups
                .SelectMany(g => g.Children.OfTypeLoadoutItemWithTargetPath())
                .Where(row => paths.Any(chosen => IsAtOrUnder(row.TargetPath, chosen)))
                .ToArray();
        }

        var found = new List<LoadoutItemWithTargetPath.ReadOnly>();
        var unmatched = new List<GamePath>();
        foreach (var path in paths)
        {
            var exact = LoadoutItemWithTargetPath.FindByTargetPath(db, path.ToGamePathParentTuple(request.LoadoutId))
                .Where(row => row.AsLoadoutItem().LoadoutId == request.LoadoutId)
                .ToArray();
            if (exact.Length > 0) found.AddRange(exact);
            else unmatched.Add(path);
        }

        // The index has no prefix lookup, so a path with no row of its own may be a folder: one scan for all of them.
        if (unmatched.Count > 0)
        {
            found.AddRange(LoadoutItem.FindByLoadout(db, request.LoadoutId)
                .OfTypeLoadoutItemWithTargetPath()
                .Where(row => unmatched.Any(chosen => IsAtOrUnder(row.TargetPath, chosen))));
        }

        return found;
    }

    private static bool IsAtOrUnder(GamePath path, GamePath chosen) =>
        path.LocationId == chosen.LocationId && (path.Path == chosen.Path || path.Path.InFolder(chosen.Path));

    private FileRemovalRow? ToRow(IDb db, LoadoutItemWithTargetPath.ReadOnly row, IReadOnlySet<EntityId> winners, bool scoped)
    {
        var item = row.AsLoadoutItem();
        if (!item.HasParent()) return null;

        var parents = item.GetThisAndParents().ToArray();
        var group = parents[1];
        var path = (GamePath)row.TargetPath;

        string? collectionName = null;
        string? lockReason = null;
        foreach (var ancestor in parents.Skip(1))
        {
            var collection = CollectionGroup.Load(db, ancestor.Id);
            if (!collection.IsValid()) continue;
            collectionName = ancestor.Name;
            lockReason = ownership.WhyLocked(collection);
            break;
        }

        return new FileRemovalRow(
            RowId: row.Id,
            Path: path,
            GroupName: group.Name,
            GroupId: LoadoutItemGroupId.From(group.Id),
            CollectionName: collectionName,
            LockReason: lockReason,
            Blocker: BlockerFor(row, parents, path, scoped),
            IsDisabled: parents.Any(p => p.IsDisabled),
            IsExternalChange: LoadoutOverridesGroup.Load(db, group.Id).IsValid(),
            IsWinner: winners.Contains(row.Id));
    }

    private static string? BlockerFor(LoadoutItemWithTargetPath.ReadOnly row, LoadoutItem.ReadOnly[] parents, GamePath path, bool scoped)
    {
        if (parents.Any(p => GameInstalledGroup.IsInstalledContent(p) || GameInstalledGroup.InstalledFor.TryGetValue(p, out _)))
            return $"{path.Path} was installed by the game and stays.";

        // On External Changes a user selects a recorded delete and removes it to un-record it; the preview must not.
        if (!scoped && row.TryGetAsDeletedFile(out _))
            return $"{path.Path} already records a delete here and stays.";

        return null;
    }
}

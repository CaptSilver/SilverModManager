using DynamicData.Kernel;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Overrides;

/// <summary>
/// Re-parents rows of a loadout's Overrides group into another group. A row keeps its id and its
/// <see cref="LoadoutFile"/> data, so the file bytes stay rooted and undo reverts the move through
/// <see cref="LoadoutItem.Parent"/>.
/// </summary>
internal sealed class OverridesMover : IOverridesMover
{
    private readonly IConnection _connection;
    private readonly Func<Loadout.ReadOnly, ILoadoutSynchronizer> _synchronizerFor;

    public OverridesMover(IConnection connection) : this(connection, static loadout => loadout.InstallationInstance.GetGame().Synchronizer) { }

    /// <summary>For tests that need a game with its own opinion on <see cref="ILoadoutSynchronizer.CanMoveOverrides"/>.</summary>
    internal OverridesMover(IConnection connection, Func<Loadout.ReadOnly, ILoadoutSynchronizer> synchronizerFor)
    {
        _connection = connection;
        _synchronizerFor = synchronizerFor;
    }

    public async Task<LoadoutItemGroupId> CreateEmptyGroup(LoadoutId loadoutId, string name)
    {
        using var tx = _connection.BeginTransaction();
        var group = LoadoutManager.AddEmptyGroup(tx, loadoutId, name);
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group].Value);
    }

    public OverridesMovePreview[] Preview(LoadoutId loadoutId, GamePath[] paths, LoadoutItemGroupId target)
    {
        var db = _connection.Db;
        var loadout = Loadout.Load(db, loadoutId);
        RequireValidTarget(db, loadoutId, target);

        var overrides = OverridesGroup(db, loadout);
        var otherRows = EnabledRowsOutsideOverrides(db, loadoutId, target).ToLookup(r => (GamePath)r.TargetPath);
        var targetEnabled = LoadoutItemGroup.Load(db, target).AsLoadoutItem().IsEnabled();

        return paths.Distinct().Select(path =>
        {
            var row = FindInGroup(db, overrides, path);
            if (!row.HasValue)
                return new OverridesMovePreview(path, false, false, null, false, false, null, null, false);

            var existing = FindInGroup(db, Optional.Some(target.Value), path);
            var winner = WinnerAfterMove(db, loadoutId, otherRows[path], target, targetEnabled);
            return new OverridesMovePreview(
                path,
                InOverrides: true,
                IsDelete: row.Value.TryGetAsDeletedFile(out _),
                Hash: HashOf(row.Value),
                TargetHasPath: existing.HasValue,
                TargetIsDelete: existing.HasValue && existing.Value.TryGetAsDeletedFile(out _),
                TargetHash: existing.HasValue ? HashOf(existing.Value) : null,
                WinnerAfterMove: winner,
                WinsAfterMove: winner == target);
        }).ToArray();
    }

    public async Task<OverridesMoveResult> Move(LoadoutId loadoutId, GamePath[] paths, LoadoutItemGroupId target, MoveOptions options)
    {
        var db = _connection.Db;
        var loadout = Loadout.Load(db, loadoutId);
        RequireValidTarget(db, loadoutId, target);

        var overrides = OverridesGroup(db, loadout);
        var synchronizer = _synchronizerFor(loadout);
        var distinct = paths.Distinct().ToArray();

        // Check every path before the first write, so a refusal never leaves half a move behind.
        var rows = new List<(GamePath Path, LoadoutItemWithTargetPath.ReadOnly Row)>();
        foreach (var path in distinct)
        {
            var row = FindInGroup(db, overrides, path);
            if (!row.HasValue)
                throw new OverridesMoveRefusedException($"`{path.Path}` is no longer among the external changes, so it was not moved.");
            if (!synchronizer.CanMoveOverrides(loadout, path))
                throw new OverridesMoveRefusedException($"This game does not allow `{path.Path}` to be moved into a mod.");
            rows.Add((path, row.Value));
        }

        var moved = new List<GamePath>();
        var skipped = new List<GamePath>();
        var pins = LoadoutFileWinner.FindByLoadout(db, loadoutId).ToLookup(p => (p.TargetPath.Item2, p.TargetPath.Item3));

        using var tx = _connection.BeginTransaction();
        foreach (var (path, row) in rows)
        {
            var existing = FindInGroup(db, Optional.Some(target.Value), path);
            if (existing.HasValue)
            {
                if (!options.ReplaceExisting)
                {
                    skipped.Add(path);
                    continue;
                }

                tx.Delete(existing.Value, recursive: false);
            }

            tx.Add(row.Id, LoadoutItem.Parent, target);
            moved.Add(path);

            if (options.PinMovedPaths)
                Pin(tx, loadoutId, path, target, pins[(path.LocationId, path.Path)].ToArray());
        }

        if (moved.Count > 0)
            await tx.Commit();

        return new OverridesMoveResult(moved.ToArray(), skipped.ToArray());
    }

    /// <summary>One pin per path: an existing pin is pointed at the target instead of getting a second row.</summary>
    private static void Pin(ITransaction tx, LoadoutId loadoutId, GamePath path, LoadoutItemGroupId target, LoadoutFileWinner.ReadOnly[] existing)
    {
        if (existing.Length > 0)
        {
            tx.Add(existing[0].Id, LoadoutFileWinner.WinnerGroup, target);
            return;
        }

        _ = new LoadoutFileWinner.New(tx)
        {
            LoadoutId = loadoutId,
            TargetPath = path.ToGamePathParentTuple(loadoutId.Value),
            WinnerGroupId = target,
        };
    }

    /// <summary>
    /// The group that provides the path once the target holds the moved file, using the winner query's rule: the
    /// highest priority of the group a row sits in wins, and a group with no priority row counts as zero.
    /// </summary>
    private static LoadoutItemGroupId? WinnerAfterMove(
        IDb db,
        LoadoutId loadoutId,
        IEnumerable<LoadoutItemWithTargetPath.ReadOnly> otherRows,
        LoadoutItemGroupId target,
        bool targetEnabled)
    {
        var priorities = LoadoutItemGroupPriority.FindByLoadout(db, loadoutId)
            .ToDictionary(p => p.TargetId, p => p.Priority.Value);

        ulong PriorityOf(LoadoutItemGroupId group) => priorities.GetValueOrDefault(group, 0UL);

        var candidates = otherRows.Select(r => LoadoutItemGroupId.From(r.AsLoadoutItem().ParentId.Value)).Distinct().ToList();
        if (targetEnabled && !candidates.Contains(target)) candidates.Add(target);
        if (candidates.Count == 0) return null;

        // Equal priorities cannot be told apart by the query either; the target is preferred so the answer is stable.
        return candidates.OrderByDescending(PriorityOf).ThenByDescending(g => g == target).First();
    }

    private static IEnumerable<LoadoutItemWithTargetPath.ReadOnly> EnabledRowsOutsideOverrides(IDb db, LoadoutId loadoutId, LoadoutItemGroupId target)
    {
        var overrides = LoadoutOverridesGroup.FindByOverridesFor(db, loadoutId.Value).Select(g => g.Id).ToHashSet();
        return LoadoutItem.FindByLoadout(db, loadoutId).OfTypeLoadoutItemWithTargetPath()
            .Where(r => r.AsLoadoutItem().HasParent()
                        && !overrides.Contains(r.AsLoadoutItem().ParentId.Value)
                        && r.AsLoadoutItem().IsEnabled());
    }

    private static Optional<EntityId> OverridesGroup(IDb db, Loadout.ReadOnly loadout)
    {
        foreach (var group in LoadoutOverridesGroup.FindByOverridesFor(db, loadout.Id))
            return group.Id;
        return Optional<EntityId>.None;
    }

    private static Optional<LoadoutItemWithTargetPath.ReadOnly> FindInGroup(IDb db, Optional<EntityId> group, GamePath path)
    {
        if (!group.HasValue) return Optional<LoadoutItemWithTargetPath.ReadOnly>.None;
        return LoadoutItemGroup.Load(db, group.Value).Children.OfTypeLoadoutItemWithTargetPath()
            .FirstOrOptional(c => c.TargetPath == path);
    }

    private static Hash? HashOf(LoadoutItemWithTargetPath.ReadOnly row) =>
        row.TryGetAsLoadoutFile(out var file) && !row.TryGetAsDeletedFile(out _) ? file.Hash : null;

    private static void RequireValidTarget(IDb db, LoadoutId loadoutId, LoadoutItemGroupId target)
    {
        var group = LoadoutItemGroup.Load(db, target);
        if (!group.IsValid() || group.AsLoadoutItem().LoadoutId != loadoutId)
            throw new OverridesMoveRefusedException("That mod is not part of this loadout.");
        if (group.TryGetAsLoadoutOverridesGroup(out _))
            throw new OverridesMoveRefusedException("The external changes cannot be moved into themselves; pick a mod.");
    }
}

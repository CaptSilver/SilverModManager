using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Overrides;

/// <summary>
/// Moves Overrides rows into the marked, disabled set-aside group, then takes the files off disk and applies. The apply
/// has to follow in the same call: with the row moved and the file gone, the next sync would otherwise see a file the
/// loadout has bytes for and the previous state has, and turn an enable or a revert into a recorded delete.
/// </summary>
internal sealed class SetAsideFiles(
    IConnection connection,
    IOverridesMover mover,
    IGameFolderFileRemoval removal,
    ISynchronizerService sync,
    ILogger<SetAsideFiles> logger) : ISetAsideFiles
{
    public async Task<SetAsideResult> SetAside(Loadout.ReadOnly loadout, IReadOnlyList<GamePath> paths, CancellationToken token)
    {
        var distinct = paths.Distinct().ToArray();
        var files = OverridesFiles(loadout, distinct);

        // Both checks come before the restore point, so a refusal leaves no trace in the revision list.
        await removal.EnsureStored(loadout, files, token);
        var restorePoint = await LoadoutSnapshot.WriteRestorePoint(connection, loadout.LoadoutId);

        var group = await FindOrCreateGroup(loadout.LoadoutId);
        var moved = await mover.Move(loadout.LoadoutId, distinct, group, new MoveOptions { ReplaceExisting = true });

        await removal.TakeOffDisk(Loadout.Load(connection.Db, loadout.LoadoutId), moved.Moved, token);

        var applied = await Apply(loadout.LoadoutId, token);
        return new SetAsideResult(moved.Moved, group, restorePoint, applied);
    }

    /// <summary>
    /// The path and identity of each file row the Overrides group has, refusing as one error every path that has none.
    /// A recorded delete has no bytes to keep and no file to move, so it counts as having none.
    /// </summary>
    private (GamePath Path, FileIdentity Identity)[] OverridesFiles(Loadout.ReadOnly loadout, GamePath[] paths)
    {
        var db = connection.Db;
        var overrides = OverridesGroupLookup.Find(loadout);
        var found = new List<(GamePath, FileIdentity)>();
        var refused = new List<GamePath>();
        foreach (var path in paths)
        {
            var row = LoadoutItemWithTargetPath.FindByTargetPath(db, path.ToGamePathParentTuple(loadout.LoadoutId))
                .Where(r => OverridesGroupLookup.IsInGroup(db, r.Id, overrides))
                .Select(r => LoadoutFile.Load(db, r.Id))
                .FirstOrDefault(f => f.IsValid());
            if (row.IsValid()) found.Add((path, row.Identity));
            else refused.Add(path);
        }

        if (refused.Count > 0)
            throw new OverridesMoveRefusedException($"{string.Join(", ", refused.Select(p => MarkdownText.Code(p.Path)))} {(refused.Count == 1 ? "is" : "are")} no longer among the external changes as a file, so nothing was set aside.");
        return found.ToArray();
    }

    private async Task<LoadoutItemGroupId> FindOrCreateGroup(LoadoutId loadoutId)
    {
        var existing = LoadoutItem.FindByLoadout(connection.Db, loadoutId).OfTypeLoadoutItemGroup()
            .Where(g => g.IsSetAsideGroup)
            .ToArray();
        if (existing.Length > 0) return existing[0].LoadoutItemGroupId;

        using var tx = connection.BeginTransaction();
        var id = LoadoutManager.AddEmptyGroup(tx, loadoutId, ISetAsideFiles.GroupName);
        tx.Add(id.Value, LoadoutItemGroup.SetAsideGroup, Null.Instance);
        tx.Add(id.Value, LoadoutItem.Disabled, Null.Instance);
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[id.Value]);
    }

    /// <returns>False when the apply was refused; the move and the file removal stand either way.</returns>
    private async Task<bool> Apply(LoadoutId loadoutId, CancellationToken token)
    {
        try
        {
            using var origin = SyncOrigin.Begin(SyncTrigger.RemoveFiles);
            await sync.Synchronize(loadoutId, cancellationToken: token);
            return true;
        }
        catch (UserFacingException e)
        {
            logger.LogWarning(e, "Applying loadout {Loadout} after setting files aside was refused; the next apply finishes it", loadoutId);
            return false;
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Update;

/// <summary>What finishing an update takes with it.</summary>
/// <param name="OldVersion">The previous list file and the archives only it uses; shared archives come back as kept. Nothing is owned while another loadout still installs the old file.</param>
/// <param name="HasUndo">Whether the group still has a restore point to give up.</param>
public sealed record ModlistFinishPlan(ModlistRemovalPlan OldVersion, bool HasUndo);

/// <summary>
/// Ends an update for good: the restore point stops being kept from garbage collection, so Undo is gone, and the old
/// version's list file and the archives only it used can be removed.
/// </summary>
public sealed class ModlistUpdateFinish(IServiceProvider provider)
{
    private IConnection Connection => provider.GetRequiredService<IConnection>();

    /// <summary>What <see cref="Finish"/> would remove, worked out first so the user can see it.</summary>
    public ModlistFinishPlan Plan(WabbajackModlistLoadoutGroup.ReadOnly group)
    {
        var db = Connection.Db;
        var current = WabbajackModlistLoadoutGroup.Load(db, group.Id);
        var hasUndo = WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(current, out _);
        if (PreviousFile(current) is not { } previous)
            return new ModlistFinishPlan(new ModlistRemovalPlan(default, "", [], [], [], [], Size.Zero, []), hasUndo);

        var plan = provider.GetRequiredService<ModlistRemovalPlanner>().Plan(LibraryFileId.From(previous.Id), CollectionGroupId.From(current.Id));
        // Another loadout still installs from the old file, or has an update of its own that goes back to it. An
        // install reads its archives again when it is resumed, and an undo reverts to the old list's rows.
        var reason = plan.Groups.Count > 0 ? $"the old version is still installed in {string.Join(", ", plan.LoadoutNames)}"
            : plan.UnfinishedUpdates.Count > 0 ? $"the update in {string.Join(", ", plan.UnfinishedUpdates)} is not finished"
            : null;
        if (reason is null) return new ModlistFinishPlan(plan, hasUndo);

        var stillNeeded = plan.OwnedArchives.Select(id => new KeptArchive(LibraryItem.Load(db, id.Value).Name, reason));
        return new ModlistFinishPlan(plan with { OwnedArchives = [], OwnedSize = Size.Zero, Kept = plan.Kept.Concat(stillNeeded).ToArray() }, hasUndo);
    }

    /// <summary>
    /// Forgets the previous version and releases the restore point. With <paramref name="deleteOldArchives"/> it then
    /// removes the old list file and the archives only it used, unless another loadout still installs the old file or
    /// has an unfinished update that goes back to it.
    /// The group is released first: removing the old file while the group still names it would take the group with it.
    /// </summary>
    /// <exception cref="ModlistUpdateRefusedException">A job is writing into the loadout or the update has not finished: releasing the restore point over a half-written version would lose the way back.</exception>
    public async Task Finish(WabbajackModlistLoadoutGroup.ReadOnly group, bool deleteOldArchives, CancellationToken ct)
    {
        var db = Connection.Db;
        var current = WabbajackModlistLoadoutGroup.Load(db, group.Id);
        var loadout = ModlistUpdater.LoadoutOf(current);
        if (provider.GetRequiredService<ModlistInstallRuns>().FindInto(loadout) is not null)
            throw new ModlistUpdateRefusedException("The modlist is being installed into this loadout. Wait for it to finish before finishing the update.");
        if (ModlistInstallRuns.IsMidUpdate(current))
            throw new ModlistUpdateRefusedException($"The update of {ModlistUpdater.NameOf(current)} is not finished. Resume it, or undo it, before finishing it.");

        var plan = Plan(current);
        var restorePoint = WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(current, out var recorded) ? TxId.From(recorded.Value) : (TxId?)null;

        ct.ThrowIfCancellationRequested();
        using (var tx = Connection.BeginTransaction())
        {
            if (WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(current, out var previous))
                tx.Retract(current.Id, WabbajackModlistLoadoutGroup.PreviousModlist, previous);
            if (restorePoint is not null)
                tx.Retract(current.Id, WabbajackModlistLoadoutGroup.LastUpdateRestorePoint, recorded);
            if (WabbajackModlistLoadoutGroup.WasDisabledByUser.TryGetValue(current, out var wasDisabled))
                tx.Retract(current.Id, WabbajackModlistLoadoutGroup.WasDisabledByUser, wasDisabled);
            // In the same commit as the retracts: once the group stops naming a restore point, nothing could find the row again.
            if (restorePoint is { } released)
                foreach (var row in ProtectionRows(Connection.Db, loadout, RestorePointChain(current.Id, released))) tx.Delete(row.Id, recursive: false);
            await tx.Commit();
        }

        if (deleteOldArchives && plan.OldVersion.Groups.Count == 0 && plan.OldVersion.UnfinishedUpdates.Count == 0 && WabbajackModlistLibraryFile.Load(Connection.Db, plan.OldVersion.Modlist.Value).IsValid())
            await provider.GetRequiredService<ModlistRemovalPlanner>().RemoveFromLibrary(plan.OldVersion, deleteArchives: true);
    }

    private WabbajackModlistLibraryFile.ReadOnly? PreviousFile(WabbajackModlistLoadoutGroup.ReadOnly group)
    {
        if (!WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(group, out var previous)) return null;
        var file = WabbajackModlistLibraryFile.Load(Connection.Db, previous);
        return file.IsValid() ? file : null;
    }

    /// <summary>
    /// The restore point and the ones before it. An update started over an unfinished one overwrites the group's
    /// recorded restore point, so the earlier one is found on the group as it was at the later one, and so on back.
    /// </summary>
    private IReadOnlyCollection<TxId> RestorePointChain(EntityId group, TxId newest)
    {
        var chain = new List<TxId>();
        for (var point = newest; !chain.Contains(point);)
        {
            chain.Add(point);
            var earlier = WabbajackModlistLoadoutGroup.Load(Connection.AsOf(point), group);
            if (!earlier.IsValid() || !WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(earlier, out var previous)) break;
            point = TxId.From(previous.Value);
        }
        return chain;
    }

    private static IEnumerable<ProtectedLoadoutRevision.ReadOnly> ProtectionRows(IDb db, LoadoutId loadout, IReadOnlyCollection<TxId> restorePoints)
        => ProtectedLoadoutRevision.FindByLoadout(db, loadout).Where(row => restorePoints.Any(point => row.Revision.Id.Value == point.Value));

    /// <summary>Deletes the garbage collection protection of <paramref name="restorePoint"/>; the loadout no longer keeps that revision's files reachable.</summary>
    internal static async Task DropProtection(IConnection connection, LoadoutId loadout, TxId restorePoint)
    {
        var rows = ProtectionRows(connection.Db, loadout, [restorePoint]).ToArray();
        if (rows.Length == 0) return;

        using var tx = connection.BeginTransaction();
        foreach (var row in rows) tx.Delete(row.Id, recursive: false);
        await tx.Commit();
    }
}

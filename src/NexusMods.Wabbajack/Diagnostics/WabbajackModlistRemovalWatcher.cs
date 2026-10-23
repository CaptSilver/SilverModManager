using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ValueSerializers;
using NexusMods.MnemonicDB.Abstractions.Internals;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Wabbajack.Models;

namespace NexusMods.Wabbajack.Diagnostics;

/// <summary>
/// Cleans up after a modlist group once it is removed. Removing a collection deletes the group's entity and its mods,
/// but the skip rows only point at it, and its tool folder and tools live outside it, so nothing else would clear them.
/// The group is gone by then, so everything is read from the datoms the removal retracted.
/// </summary>
public sealed class WabbajackModlistRemovalWatcher(IConnection connection, WabbajackPaths paths, ILogger<WabbajackModlistRemovalWatcher> logger) : IHostedService, IDisposable
{
    private IDisposable? _subscription;
    private readonly SemaphoreSlim _cleanup = new(1, 1);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null) throw new InvalidOperationException("The modlist removal watcher is already running.");

        // The retraction is only in the datoms of the revision that removed the group: the entity is gone from the db.
        var modlistAttribute = connection.Db.AttributeCache.GetAttributeId(WabbajackModlistLoadoutGroup.Modlist.Id);
        _subscription = connection.Revisions.Subscribe(db =>
        {
            var removed = new List<RemovedGroup>();
            foreach (var datom in db.RecentlyAdded)
            {
                if (!datom.IsRetract || datom.A != modlistAttribute) continue;
                // Replacing the reference also retracts the old value; only a group that is gone is cleaned up.
                if (WabbajackModlistLoadoutGroup.Load(db, datom.E).IsValid()) continue;
                removed.Add(ReadRemoved(db, datom.E));
            }
            if (removed.Count > 0) _ = CleanUp(removed);
        });
        return Task.CompletedTask;
    }

    /// <summary>What a cleanup needs of a removed group: its tools folder and the installation its tools were registered for.</summary>
    private sealed record RemovedGroup(EntityId Id, string? ExtrasFolder, EntityId? Installation);

    private RemovedGroup ReadRemoved(IDb db, EntityId group)
    {
        var cache = db.AttributeCache;
        var extrasAttribute = cache.GetAttributeId(WabbajackModlistLoadoutGroup.ExtrasFolder.Id);
        var loadoutAttribute = cache.GetAttributeId(LoadoutItem.Loadout.Id);
        var installationAttribute = cache.GetAttributeId(Loadout.Installation.Id);

        string? extras = null;
        EntityId? loadout = null;
        foreach (var datom in db.RecentlyAdded)
        {
            if (!datom.IsRetract || datom.E != group) continue;
            if (datom.A == extrasAttribute)
                extras = WabbajackModlistLoadoutGroup.ExtrasFolder.ReadValue(datom.ValueSpan, datom.Prefix.ValueTag, connection.AttributeResolver);
            else if (datom.A == loadoutAttribute)
                loadout = EntityIdSerializer.Read(datom.ValueSpan);
        }

        // The loadout survives a collection removal but not a loadout removal, which retracts it in the same revision.
        EntityId? installation = null;
        if (loadout is { } id)
        {
            var live = Loadout.Load(db, id);
            if (live.IsValid())
                installation = live.InstallationId.Value;
            else
            {
                foreach (var datom in db.RecentlyAdded)
                {
                    if (!datom.IsRetract || datom.E != id || datom.A != installationAttribute) continue;
                    installation = EntityIdSerializer.Read(datom.ValueSpan);
                }
            }
        }
        return new RemovedGroup(group, extras, installation);
    }

    private async Task CleanUp(List<RemovedGroup> groups)
    {
        await _cleanup.WaitAsync();
        try
        {
            using var tx = connection.BeginTransaction();
            var any = false;
            foreach (var group in groups)
            {
                foreach (var skipped in WabbajackSkippedFile.FindByGroup(connection.Db, group.Id))
                {
                    tx.Delete(skipped.Id, recursive: false);
                    any = true;
                }
                any |= RemoveExtras(tx, group);
            }
            if (any) await tx.Commit();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not clean up after a removed modlist");
        }
        finally
        {
            _cleanup.Release();
        }
    }

    /// <summary>
    /// Deletes the group's tools folder and the tools registered from it. A recorded folder that is not inside the
    /// extras root is left alone, along with its tools: the value is data, not a promise that the folder is ours.
    /// </summary>
    private bool RemoveExtras(ITransaction tx, RemovedGroup group)
    {
        if (string.IsNullOrEmpty(group.ExtrasFolder)) return false;

        var root = System.IO.Path.GetFullPath(paths.ExtrasRoot.ToString()).TrimEnd('/');
        var folder = System.IO.Path.GetFullPath(group.ExtrasFolder).TrimEnd('/');
        if (!folder.StartsWith(root + "/", StringComparison.Ordinal))
        {
            logger.LogWarning("Not deleting '{Folder}' for a removed modlist: it is not inside the extras folder '{Root}'", folder, root);
            return false;
        }

        var any = false;
        if (group.Installation is { } installation)
        {
            foreach (var tool in UserTool.FindByInstallation(connection.Db, InstallationId(installation)))
            {
                var exe = System.IO.Path.GetFullPath(tool.Exe);
                if (!exe.StartsWith(folder + "/", StringComparison.Ordinal)) continue;
                tx.Delete(tool.Id, recursive: false);
                any = true;
            }
        }

        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not delete the tools folder '{Folder}' of a removed modlist", folder);
        }
        return any;
    }

    private static GameInstallMetadataId InstallationId(EntityId id) => GameInstallMetadataId.From(id.Value);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => Interlocked.Exchange(ref _subscription, null)?.Dispose();
}

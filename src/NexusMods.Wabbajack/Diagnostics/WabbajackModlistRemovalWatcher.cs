using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Wabbajack.Models;

namespace NexusMods.Wabbajack.Diagnostics;

/// <summary>
/// Deletes the rows that hang off a modlist group once the group is removed. Removing a collection deletes the
/// group's entity and its mods, but the skip rows only point at it, so nothing else would ever clear them.
/// </summary>
public sealed class WabbajackModlistRemovalWatcher(IConnection connection, ILogger<WabbajackModlistRemovalWatcher> logger) : IHostedService, IDisposable
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
            var removed = new List<EntityId>();
            foreach (var datom in db.RecentlyAdded)
            {
                if (!datom.IsRetract || datom.A != modlistAttribute) continue;
                // Replacing the reference also retracts the old value; only a group that is gone is cleaned up.
                if (WabbajackModlistLoadoutGroup.Load(db, datom.E).IsValid()) continue;
                removed.Add(datom.E);
            }
            if (removed.Count > 0) _ = CleanUp(removed);
        });
        return Task.CompletedTask;
    }

    private async Task CleanUp(List<EntityId> groups)
    {
        await _cleanup.WaitAsync();
        try
        {
            using var tx = connection.BeginTransaction();
            var any = false;
            foreach (var group in groups)
            {
                foreach (var skipped in WabbajackSkippedFile.FindByGroup(connection.Db, group))
                {
                    tx.Delete(skipped.Id, recursive: false);
                    any = true;
                }
            }
            if (any) await tx.Commit();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not remove the skipped-file rows of a removed modlist");
        }
        finally
        {
            _cleanup.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => Interlocked.Exchange(ref _subscription, null)?.Dispose();
}

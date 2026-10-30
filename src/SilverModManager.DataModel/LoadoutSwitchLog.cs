using System.Reactive.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel;

/// <summary>
/// Logs every mod, collection or group that is switched on or off. A switch is a one-datom write that changes what
/// the next apply puts in the game folder, and nothing else records it.
/// </summary>
/// <remarks>
/// Watches the revision stream for <see cref="LoadoutItem.Disabled"/> instead of logging where the screens write it:
/// the sidebar, the collection and loadout pages, the Health Check fix, collection installs and updates and the load
/// order sinks all set the marker, and a path added later is covered too. An item that was deleted with its marker
/// is not a switch.
/// </remarks>
public sealed class LoadoutSwitchLog(IConnection connection, ILogger<LoadoutSwitchLog> logger) : IHostedService, IDisposable
{
    /// <summary>More switches than this in one write are summarised in one line.</summary>
    private const int ListedIndividually = 5;
    private const int NamesInSummary = 3;

    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The stream's first value is the current revision, which holds nothing new.
        _subscription = connection.Revisions.Skip(1).Subscribe(Observe);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();

    private void Observe(IDb db)
    {
        // Runs inside the revision stream: a failure here must never reach the writer.
        try
        {
            var marker = db.AttributeCache.GetAttributeId(LoadoutItem.Disabled.Id);
            var switches = new List<(EntityId Item, bool On)>();
            foreach (var datom in db.RecentlyAdded)
            {
                if (datom.A == marker) switches.Add((datom.E, On: datom.IsRetract));
            }

            if (switches.Count == 0) return;
            Log(db, switches);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not log a change to which mods are switched on");
        }
    }

    private void Log(IDb db, List<(EntityId Item, bool On)> switches)
    {
        var named = new List<(LoadoutItem.ReadOnly Item, bool On)>();
        foreach (var (id, on) in switches)
        {
            var item = LoadoutItem.Load(db, id);

            // Deleting an item retracts its marker along with everything else.
            if (!item.IsValid()) continue;
            named.Add((item, on));
        }

        if (named.Count == 0) return;

        foreach (var direction in new[] { true, false })
        {
            var group = named.Where(n => n.On == direction).Select(static n => n.Item).ToArray();
            if (group.Length == 0) continue;

            var verb = direction ? "Switched on" : "Switched off";
            if (group.Length <= ListedIndividually)
            {
                foreach (var item in group)
                    logger.LogInformation("{Verb} {Kind} '{Name}' in loadout {Loadout} ({LoadoutId}){Contents}", verb, KindOf(item), item.Name, item.Loadout.Name, item.Loadout.LoadoutId, ContentsOf(db, item));
            }
            else
            {
                var loadout = group[0].Loadout;
                var names = string.Join(", ", group.Take(NamesInSummary).Select(static i => $"'{i.Name}'"));
                logger.LogInformation("{Verb} {Count} items in loadout {Loadout} ({LoadoutId}), among them {Names}", verb, group.Length, loadout.Name, loadout.LoadoutId, names);
            }
        }
    }

    private static string KindOf(LoadoutItem.ReadOnly item)
    {
        if (!item.TryGetAsLoadoutItemGroup(out var group)) return "item";
        return group.TryGetAsCollectionGroup(out _) ? "collection" : "mod";
    }

    private static string ContentsOf(IDb db, LoadoutItem.ReadOnly item)
    {
        if (!item.TryGetAsLoadoutItemGroup(out var group)) return "";
        var count = LoadoutItem.FindByParent(db, group.LoadoutItemGroupId).Count;
        return count == 0 ? "" : $", {count} items inside";
    }
}

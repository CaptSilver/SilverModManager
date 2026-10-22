using System.Collections.Frozen;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Diagnostics.References;

using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.Emitters;

public class MissingMasterEmitter : ILoadoutDiagnosticEmitter
{
    private readonly ICreationEngineGame _game;
    public MissingMasterEmitter(ICreationEngineGame game)
    {
        _game = game;
    }
    
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        return Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);
    }

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, CancellationToken cancellationToken)
    {
        await Task.Yield();
        
        var plugins = syncTree.Where(node =>
            node.Value.HaveLoadout &&
            KnownCEExtensions.Plugins.Contains(node.Key.Extension) &&
            node.Key.Parent == _game.DataPath);
        
        // Index the plugins in the loadout
        Dictionary<string, LoadoutItemGroup.ReadOnly> pluginsInLoadout = new(PluginNameComparer.Instance);
        foreach (var item in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var path = (GamePath)item.TargetPath;
            if (path.LocationId == LocationId.Game && path.Parent == _game.DataPath && KnownCEExtensions.Plugins.Contains(path.Extension))
                pluginsInLoadout[path.FileName] = item.AsLoadoutItem().Parent;

        }

        // Index the plugins that will be on-disk in the game folder
        Dictionary<string, (SyncNode Node, PluginHeader Header)> pluginHeaders = new(PluginNameComparer.Instance);
        List<string> unreadable = [];
        foreach (var (key, node) in plugins)
        {
            var header = await _game.ParseNode(node, node.Loadout.Hash, key.Path.FileName);
            if (header == null)
            {
                unreadable.Add(key.Path.FileName);
                continue;
            }
            pluginHeaders[header.FileName] = (node, header);
        }

        // Their masters can't be checked, and the plugins would be missing from the load order.
        if (unreadable.Count > 0)
        {
            unreadable.Sort(StringComparer.OrdinalIgnoreCase);
            yield return Diagnostics.CreateUnreadablePlugins(unreadable.Count, string.Join(", ", unreadable));
        }
        var unreadableNames = unreadable.ToHashSet(PluginNameComparer.Instance);

        // For each on-disk plugin, check if it has all the required masters
        foreach (var header in pluginHeaders)
        {
            foreach (var master in header.Value.Header.Masters)
            {
                // If the master is already in the loadout, skip it
                if (pluginHeaders.ContainsKey(master))
                    continue;

                // An unreadable master is still in the game folder; it has its own diagnostic above
                if (unreadableNames.Contains(master))
                    continue;

                // If the master is disabled, emit a diagnostic
                if (pluginsInLoadout.TryGetValue(master, out var disabled))
                {
                    yield return Diagnostics.CreateMissingMasterIsDisabled(
                        new LoadoutItemGroupReference()
                        {
                            TxId = loadout.Db.BasisTxId,
                            DataId = header.Value.Node.Loadout.EntityId
                        },
                        header.Key,
                        master,
                        new LoadoutItemGroupReference()
                            {
                                TxId = loadout.Db.BasisTxId,
                                DataId = disabled.LoadoutItemGroupId,
                            }
                    );
                }
                // Otherwise, emit a diagnostic that the master is missing
                else
                {
                    yield return Diagnostics.CreateMissingMaster(
                        new LoadoutItemGroupReference()
                        {
                            TxId = loadout.Db.BasisTxId,
                            DataId = header.Value.Node.Loadout.EntityId
                        },
                        header.Key,
                        master
                    );
                }
            }
        }

    }
}

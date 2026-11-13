using System.Collections.Frozen;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Emitters;

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
        var installation = loadout.InstallationInstance;
        var overrides = OverridesGroupLookup.Find(loadout);
        // The mod link names the group holding the plugin's loadout row. A plugin with no row of its own (a file found
        // in the game folder) falls back to the Overrides group. A loadout with neither still gets the diagnostic,
        // since the master is missing regardless, but it links no mod: a reference to the zero id would show as an
        // invalid entity.
        EntityId? GroupOf(in SyncNode node) => OwningGroup.Of(loadout.Db, node) ?? (overrides.Value == 0 ? null : overrides);
        LoadoutItemGroupReference ReferenceTo(EntityId id) => new()
        {
            TxId = loadout.Db.BasisTxId,
            DataId = LoadoutItemGroupId.From(id),
        };
        // The templates require a mod reference, so a diagnostic with no group gets the zero one and drops it here.
        Diagnostic Linked(Diagnostic diagnostic, EntityId? group)
        {
            if (group is null) diagnostic.DataReferences.Remove(DataReferenceDescription.Mod);
            return diagnostic;
        }
        foreach (var (key, node) in plugins)
        {
            var header = await _game.ParseNode(node, node.Loadout.Identity, key, installation);
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

        var externalMasters = new Dictionary<string, bool>(PluginNameComparer.Instance);

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
                    var group = GroupOf(header.Value.Node);
                    yield return Linked(Diagnostics.CreateMissingMasterIsDisabled(
                        ReferenceTo(group ?? default),
                        header.Key,
                        master,
                        ReferenceTo(disabled.Id)
                    ), group);
                }
                // Otherwise it is missing, unless the game loads it from a plugin directory the loadout does not
                // deploy into (OpenMW reads the game's masters from its data= folders).
                else
                {
                    if (!externalMasters.TryGetValue(master, out var external))
                        externalMasters[master] = external = await IsInExternalPluginDirectory(installation, master);
                    if (external)
                        continue;

                    var group = GroupOf(header.Value.Node);
                    yield return Linked(Diagnostics.CreateMissingMaster(
                        ReferenceTo(group ?? default),
                        header.Key,
                        master
                    ), group);
                }
            }
        }

    }

    /// <summary>
    /// Searches the game's plugin directories from the last, and reads the header of the first file found. The folder
    /// the loadout deploys into is skipped: what will be there is the tree's loadout side, and a file on disk there
    /// with no loadout row is either a removed mod's file the next apply deletes or one the sync has not ingested yet.
    /// </summary>
    private async ValueTask<bool> IsInExternalPluginDirectory(GameInstallation installation, string master)
    {
        // A master name comes from a plugin's header, so it is joined to a folder only when it is a bare file name.
        if (!ConditionPaths.IsPlainFileName(master)) return false;

        var name = RelativePath.FromUnsanitizedInput(master);
        var deployRoot = installation.Locations.ToAbsolutePath(_game.DataPath);
        var directories = _game.PluginDirectories(installation);
        for (var i = directories.Count - 1; i >= 0; i--)
        {
            if (directories[i] == deployRoot) continue;

            var file = directories[i] / name;
            if (file.FileExists)
                return await _game.ParsePluginFile(file, name) is not null;
        }

        return false;
    }
}

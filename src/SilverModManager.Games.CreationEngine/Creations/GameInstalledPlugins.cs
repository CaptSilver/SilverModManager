using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.Creations;

/// <summary>
/// The files in the data folder that the game installed itself, and whether the "Installed by the game" group is
/// switched on. The plugin list writer needs both: the group's switch decides these plugins, not their sort rows.
/// </summary>
/// <param name="Names">File names directly in the data folder, plugins and archives alike; only plugin names are ever looked up.</param>
/// <param name="GroupEnabled">Whether the group is on. True when the loadout has none.</param>
public sealed record GameInstalledPlugins(IReadOnlySet<string> Names, bool GroupEnabled)
{
    public static readonly GameInstalledPlugins None = new(new HashSet<string>(PluginNameComparer.Instance), GroupEnabled: true);

    public bool Contains(string name) => Names.Contains(name);

    public static GameInstalledPlugins For(IDb db, LoadoutId loadout, GamePath dataPath)
    {
        var names = new HashSet<string>(PluginNameComparer.Instance);
        foreach (var path in GameInstalledPaths.For(db, loadout).Keys)
        {
            if (path.Parent == dataPath) names.Add(path.FileName.ToString());
        }

        var groupEnabled = GameInstalledGroup.FindByInstalledFor(db, loadout)
            .All(group => !group.AsLoadoutItemGroup().AsLoadoutItem().IsDisabled);
        return new GameInstalledPlugins(names, groupEnabled);
    }
}

using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.CreationEngine.Models;

/// <summary>
/// The plugin load order of a Creation Engine loadout.
/// </summary>
[PublicAPI]
[Include<SilverModManager.Abstractions.Loadouts.SortOrder>]
public partial class PluginSortOrder : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.CreationEngine.PluginSortOrder";

    /// <summary>
    /// Marks an entity as a plugin sort order, so it can be queried until the database can filter on included models.
    /// Has to be set to true when the model is created.
    /// </summary>
    public static readonly MarkerAttribute Marker = new(Namespace, nameof(Marker));

    /// <summary>
    /// The order was first filled from the plugin list the game keeps on disk, rather than built from the loadout alone.
    /// </summary>
    public static readonly BooleanAttribute SeededFromGameFile = new(Namespace, nameof(SeededFromGameFile));
}

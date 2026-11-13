using JetBrains.Annotations;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.CreationEngine.Models;

/// <summary>
/// One plugin in a <see cref="PluginSortOrder"/>.
/// </summary>
[PublicAPI]
[Include<SortOrderItem>]
public partial class PluginSortOrderItem : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.CreationEngine.PluginSortableEntry";

    /// <summary>
    /// The plugin's file name, the identifier the game's plugin list uses. Compare it ignoring case.
    /// </summary>
    public static readonly StringAttribute PluginName = new(Namespace, nameof(PluginName));

    /// <summary>
    /// Whether the plugin is switched on in the load order.
    /// </summary>
    public static readonly BooleanAttribute IsEnabled = new(Namespace, nameof(IsEnabled));

    /// <summary>
    /// Whether OpenMW loads the plugin as groundcover (a <c>groundcover=</c> line) instead of as content. Absent
    /// means off; only the OpenMW game ever sets it.
    /// </summary>
    public static readonly BooleanAttribute Groundcover = new(Namespace, nameof(Groundcover)) { IsOptional = true };
}

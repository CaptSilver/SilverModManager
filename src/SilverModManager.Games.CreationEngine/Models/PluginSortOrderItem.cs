using JetBrains.Annotations;
using NexusMods.Abstractions.Games;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;

namespace NexusMods.Games.CreationEngine.Models;

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
}

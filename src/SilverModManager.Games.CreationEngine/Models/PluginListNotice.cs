using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.CreationEngine.Models;

/// <summary>
/// A note that the app replaced the plugin list the game had on disk, kept so the Load Order page can say so.
/// </summary>
[PublicAPI]
public partial class PluginListNotice : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.CreationEngine.PluginListNotice";

    /// <summary>
    /// The plugin sort order whose list replaced the game's.
    /// </summary>
    public static readonly ReferenceAttribute<SilverModManager.Abstractions.Loadouts.SortOrder> SortOrder = new(Namespace, nameof(SortOrder))
    {
        IsIndexed = true,
    };

    /// <summary>
    /// What was replaced, in a sentence the page can show as is.
    /// </summary>
    public static readonly StringAttribute Summary = new(Namespace, nameof(Summary));

    /// <summary>
    /// When the game's list was replaced.
    /// </summary>
    public static readonly TimestampAttribute ReplacedAt = new(Namespace, nameof(ReplacedAt));
}

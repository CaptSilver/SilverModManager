using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// Represents a group of items.
/// </summary>
[PublicAPI]
[Include<LoadoutItem>]
public partial class LoadoutItemGroup : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.LoadoutItemGroup";

    /// <summary>
    /// Marker.
    /// </summary>
    public static readonly MarkerAttribute Group = new(Namespace, nameof(Group)) { IsIndexed = true };

    /// <summary>
    /// Children of the group.
    /// </summary>
    public static readonly BackReferenceAttribute<LoadoutItem> Children = new(LoadoutItem.Parent);
}

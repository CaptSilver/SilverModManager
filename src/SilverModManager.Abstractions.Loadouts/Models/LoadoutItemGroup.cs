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
    /// Marks the group that holds the game's own settings files (INIs a Wabbajack list keeps in its MO2 profile).
    /// Lookups go by the marker, so the group's name is free text the user can change.
    /// </summary>
    public static readonly MarkerAttribute GameSettingsGroup = new(Namespace, nameof(GameSettingsGroup)) { IsIndexed = true };

    /// <summary>
    /// Children of the group.
    /// </summary>
    public static readonly BackReferenceAttribute<LoadoutItem> Children = new(LoadoutItem.Parent);
}

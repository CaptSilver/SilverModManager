using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// Represents an item inside a Loadout.
/// </summary>
[PublicAPI]
public partial class LoadoutItem : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.LoadoutItem";

    /// <summary>
    /// Name of the item.
    /// </summary>
    public static readonly StringAttribute Name = new(Namespace, nameof(Name));

    /// <summary>
    /// Marker to signal that the item is disabled. A disabled loadout item will
    /// not participate in actions.
    /// </summary>
    /// <remarks>
    /// The exact meaning of a "disabled" loadout item is up to the implementations.
    /// </remarks>
    public static readonly MarkerAttribute Disabled = new(Namespace, nameof(Disabled)) { IsIndexed = true };

    /// <summary>
    /// Marker that lets the item, and everything below it, deploy to paths the synchronizer otherwise protects,
    /// such as the game's save folder. Some mods are nothing but save files. Without the marker such files are
    /// skipped; with it they are written unless a file of the user's own is already there.
    /// </summary>
    public static readonly MarkerAttribute ProtectedPathsAllowed = new(Namespace, nameof(ProtectedPathsAllowed)) { IsIndexed = true };

    /// <summary>
    /// Loadout that contains the item.
    /// </summary>
    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };

    /// <summary>
    /// Optional parent of the item.
    /// </summary>
    public static readonly ReferenceAttribute<LoadoutItemGroup> Parent = new(Namespace, nameof(Parent)) { IsIndexed = true, IsOptional = true };

    [PublicAPI]
    public partial struct ReadOnly
    {
        /// <summary>
        /// True if this item contains a parent, else false.
        /// </summary>
        public bool HasParent() => this.Contains(LoadoutItem.Parent);
    }
}

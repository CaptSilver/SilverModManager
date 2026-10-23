using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Abstractions.NexusModsLibrary.Models;

/// <summary>
/// A collection category.
/// </summary>
public partial class CollectionCategory : IModelDefinition
{
    private const string Namespace = "SilverModManager.NexusModsLibrary.CollectionCategory";

    /// <summary>
    /// The name of the collection tag.
    /// </summary>
    public static readonly StringAttribute Name = new(Namespace, nameof(Name)) { IsIndexed = true };

    /// <summary>
    /// The Nexus mods id of the collection tag.
    /// </summary>
    public static readonly UInt64Attribute NexusId = new(Namespace, nameof(NexusId)) { IsIndexed = true };
}

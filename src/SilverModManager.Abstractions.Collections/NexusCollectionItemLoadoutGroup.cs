using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Abstractions.Collections;

[Include<LoadoutItemGroup>]
public partial class NexusCollectionItemLoadoutGroup : IModelDefinition
{
    private const string Namespace = "SilverModManager.Collections.NexusCollectionItemLoadoutGroup";

    /// <summary>
    /// Whether the item is required or optional.
    /// </summary>
    public static readonly BooleanAttribute IsRequired = new(Namespace, nameof(IsRequired)) { IsIndexed = true };

    /// <summary>
    /// Reference to the original download.
    /// </summary>
    public static readonly ReferenceAttribute<CollectionDownload> Download = new(Namespace, nameof(Download));

    public partial struct ReadOnly
    {
        /// <summary>
        /// Whether the item is optional or required.
        /// </summary>
        public bool IsOptional => !IsRequired;
    }
}

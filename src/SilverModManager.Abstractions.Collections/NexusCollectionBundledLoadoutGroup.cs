using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Abstractions.Collections;

/// <summary>
/// A mod that was bundled with a collection
/// </summary>
[Include<NexusCollectionItemLoadoutGroup>]
public partial class NexusCollectionBundledLoadoutGroup : IModelDefinition
{
    private const string Namespace = "SilverModManager.Collections.NexusCollectionBundledLoadoutGroup";

    /// <summary>
    /// The downloaded collection archive that this mod was bundled with
    /// </summary>
    public static readonly ReferenceAttribute<NexusModsCollectionLibraryFile> CollectionLibraryFile = new(Namespace, nameof(CollectionLibraryFile));

    /// <summary>
    /// Reference to the original download.
    /// </summary>
    public static readonly ReferenceAttribute<CollectionDownloadBundled> BundleDownload = new(Namespace, nameof(BundleDownload));
}

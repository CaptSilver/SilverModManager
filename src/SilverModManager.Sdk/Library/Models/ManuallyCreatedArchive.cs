using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Sdk.Library;

[PublicAPI]
[Include<LibraryArchive>]
public partial class ManuallyCreatedArchive : IModelDefinition
{
    public enum CreationSource
    {
        /// <summary>
        /// This mod was created from files in a downloaded collection. This normally means the user
        /// cloned the collection and so bundled mods were removed and added to this mod.
        /// </summary>
        CollectionBundled = 1,
    }
    
    private const string Namespace = "SilverModManager.Library.ManuallyCreatedArchive";

    /// <summary>
    /// The source of this archive
    /// </summary>
    public static readonly EnumAttribute<CreationSource> Source = new(Namespace, nameof(CreationSource));
}

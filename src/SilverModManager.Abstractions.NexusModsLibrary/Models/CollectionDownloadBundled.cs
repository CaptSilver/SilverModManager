using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Abstractions.NexusModsLibrary.Models;

[PublicAPI]
[Include<CollectionDownload>]
public partial class CollectionDownloadBundled : IModelDefinition
{
    private const string Namespace = "SilverModManager.NexusModsLibrary.CollectionDownloadBundled";

    /// <summary>
    /// Bundled path.
    /// </summary>
    public static readonly RelativePathAttribute BundledPath = new(Namespace, nameof(BundledPath));
}

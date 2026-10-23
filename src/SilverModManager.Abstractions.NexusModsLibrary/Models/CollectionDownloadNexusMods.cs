using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Abstractions.NexusModsLibrary.Models;

/// <summary>
/// <see cref="CollectionDownload"/> for a file on Nexus Mods.
/// </summary>
/// <remarks>
/// Source = `nexus` in the collection JSON file.
/// </remarks>
[PublicAPI]
[Include<CollectionDownload>]
public partial class CollectionDownloadNexusMods : IModelDefinition
{
    private const string Namespace = "SilverModManager.NexusModsLibrary.CollectionDownloadNexusMods";

    /// <summary>
    /// <see cref="FileUid"/>.
    /// </summary>
    public static readonly FileUidAttribute FileUid = new(Namespace, nameof(FileUid)) { IsIndexed = true };

    /// <summary>
    /// <see cref="ModUid"/>.
    /// </summary>
    public static readonly ModUidAttribute ModUid = new(Namespace, nameof(ModUid)) { IsIndexed = true };

    /// <summary>
    /// Reference to the file metadata.
    /// </summary>
    public static readonly ReferenceAttribute<NexusModsFileMetadata> FileMetadata = new(Namespace, nameof(FileMetadata));
}

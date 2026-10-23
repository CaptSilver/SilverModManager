using JetBrains.Annotations;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.Resources;

namespace SilverModManager.Abstractions.NexusModsLibrary;

/// <summary>
/// Represents a remote mod page on SilverModManager.
/// </summary>
[PublicAPI]
public partial class NexusModsModPageMetadata : IModelDefinition
{
    private const string Namespace = "SilverModManager.NexusModsLibrary.NexusModsModPageMetadata";

    /// <summary>
    /// The ID of the mod page.
    /// </summary>
    public static readonly ModUidAttribute Uid = new(Namespace, nameof(Uid)) { IsIndexed = true };

    /// <summary>
    /// The name of the mod page.
    /// </summary>
    public static readonly StringAttribute Name = new(Namespace, nameof(Name));

    /// <summary>
    /// The game of the mod page.
    /// </summary>
    /// <remarks>
    ///     This will be deprecated in the future, since V2 API only needs <see cref="Uid"/>
    ///     which contains the <see cref="NexusModsGameId"/> The <see cref="GameDomain"/> is a legacy field of the V1 API.
    /// </remarks>
    public static readonly GameDomainAttribute GameDomain = new(Namespace, nameof(GameDomain)) { IsIndexed = true };

    /// <summary>
    /// The last time the mod page was updated (UTC). This is useful for cache invalidation.
    /// </summary>
    public static readonly TimestampAttribute UpdatedAt = new(Namespace, nameof(UpdatedAt));

    /// <summary>
    /// The last time a full update of this page info was performed in the data store.
    /// This is used for local caching.
    /// </summary>
    public static readonly TimestampAttribute DataUpdatedAt = new(Namespace, nameof(DataUpdatedAt));
    
    /// <summary>
    /// Uri for the full sized picture of the mod.
    /// </summary>
    public static readonly UriAttribute FullSizedPictureUri = new(Namespace, nameof(FullSizedPictureUri)) { IsOptional = true };

    public static readonly ReferenceAttribute<PersistedDbResource> ThumbnailResource = new(Namespace, nameof(ThumbnailResource)) { IsOptional = true };

    /// <summary>
    /// Uri for the thumbnail of the full sized picture.
    /// </summary>
    public static readonly UriAttribute ThumbnailUri = new(Namespace, nameof(ThumbnailUri)) { IsOptional = true };

    /// <summary>
    /// Back-reference to all files from this page.
    /// </summary>
    public static readonly BackReferenceAttribute<NexusModsFileMetadata> Files = new(NexusModsFileMetadata.ModPage);
}

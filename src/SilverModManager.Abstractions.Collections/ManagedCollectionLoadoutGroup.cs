using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Attributes;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Abstractions.Collections;

/// <summary>
/// Represents an uploaded collection managed by the app.
/// </summary>
[Include<CollectionGroup>]
public partial class ManagedCollectionLoadoutGroup : IModelDefinition
{
    private const string Namespace = "SilverModManager.ManagedCollectionLoadoutGroup";

    /// <summary>
    /// The collection.
    /// </summary>
    public static readonly ReferenceAttribute<CollectionMetadata> Collection = new(Namespace, nameof(Collection)) { IsIndexed = true };

    /// <summary>
    /// The revision number of the last published revision.
    /// </summary>
    public static readonly RevisionNumberAttribute LastPublishedRevisionNumber = new(Namespace, nameof(LastPublishedRevisionNumber)) { IsOptional = true };

    /// <summary>
    /// The current revision number.
    /// </summary>
    public static readonly RevisionNumberAttribute CurrentRevisionNumber = new(Namespace, nameof(CurrentRevisionNumber));

    /// <summary>
    /// The ID of the current revision. Only available if the
    /// current revision has been uploaded.
    /// </summary>
    public static readonly RevisionIdAttribute CurrentRevisionId = new(Namespace, nameof(CurrentRevisionId)) { IsOptional = true };

    /// <summary>
    /// Date when revision was last uploaded.
    /// </summary>
    public static readonly TimestampAttribute LastUploadDate = new(Namespace, nameof(LastUploadDate));

    /// <summary>
    /// Creates a <see cref="RevisionStatus"/>.
    /// </summary>
    public static RevisionStatus ToStatus(RevisionNumber currentRevisionNumber, Optional<RevisionNumber> lastPublishedRevision)
    {
        if (!lastPublishedRevision.HasValue) return RevisionStatus.Draft;
        return lastPublishedRevision.Value == currentRevisionNumber ? RevisionStatus.Published : RevisionStatus.Draft;
    }

    public partial struct ReadOnly
    {
        /// <inheritdoc cref="ManagedCollectionLoadoutGroup.ToStatus"/>
        public RevisionStatus ToStatus() => ManagedCollectionLoadoutGroup.ToStatus(CurrentRevisionNumber, LastPublishedRevisionNumber);
    }
}

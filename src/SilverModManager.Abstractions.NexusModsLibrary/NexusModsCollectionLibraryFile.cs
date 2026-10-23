using JetBrains.Annotations;
using SilverModManager.Abstractions.NexusModsLibrary.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Abstractions.NexusModsLibrary;

/// <summary>
/// A library file that is a Nexus Mods collection download.
/// </summary>
[PublicAPI]
[Include<LibraryFile>]
public partial class NexusModsCollectionLibraryFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.NexusModsLibrary.NexusModsCollectionLibraryFile";

    public static readonly CollectionsSlugAttribute CollectionSlug = new(Namespace, nameof(CollectionSlug)) { IsIndexed = true };

    public static readonly RevisionNumberAttribute CollectionRevisionNumber = new(Namespace, nameof(CollectionRevisionNumber)) { IsIndexed = true };
}

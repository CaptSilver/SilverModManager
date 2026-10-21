using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Sdk.Library;

/// <summary>
/// A library file that was downloaded from the asset of a GitHub release.
/// </summary>
[PublicAPI]
[Include<LibraryFile>]
public partial class GitHubReleaseLibraryFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Library.GitHubRelease";

    // The first attribute declared decides whether an entity counts as this model (IsValid, TryGetAs, All), so it
    // must be one every row has: with the optional repository attributes first, a release added by owner and
    // name was not recognised as a GitHubReleaseLibraryFile.

    /// <summary>
    /// The tag of the release the asset belongs to. For a download of the latest release this is the tag that
    /// was latest at the time, not the word "latest".
    /// </summary>
    public static readonly StringAttribute Tag = new(Namespace, nameof(Tag)) { IsIndexed = true };

    /// <summary>
    /// The file name of the release asset.
    /// </summary>
    public static readonly StringAttribute AssetName = new(Namespace, nameof(AssetName));

    /// <summary>
    /// The SHA-256 the downloaded bytes were verified against: the digest that was pinned beforehand, or the one
    /// computed on first download when none was.
    /// </summary>
    public static readonly Sha256Attribute PinnedSha256 = new(Namespace, nameof(PinnedSha256));

    /// <summary>
    /// The numeric id of the repository. Present when the repository was addressed by id, which stays valid
    /// across renames and transfers.
    /// </summary>
    public static readonly UInt64Attribute RepositoryId = new(Namespace, nameof(RepositoryId)) { IsOptional = true, IsIndexed = true };

    /// <summary>
    /// The owner of the repository. Present when the repository was addressed by name.
    /// </summary>
    public static readonly StringAttribute Organization = new(Namespace, nameof(Organization)) { IsOptional = true };

    /// <summary>
    /// The name of the repository. Present when the repository was addressed by name.
    /// </summary>
    public static readonly StringAttribute Repository = new(Namespace, nameof(Repository)) { IsOptional = true };
}

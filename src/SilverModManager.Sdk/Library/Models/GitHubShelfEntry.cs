using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// A GitHub repository the user put on a game's shelf, or a game-declared one the app has learned something about
/// (its numeric id). Not a file: it says where releases come from, and which asset of a release to take.
/// </summary>
[PublicAPI]
public partial class GitHubShelfEntry : IModelDefinition
{
    private const string Namespace = "SilverModManager.Library.GitHubShelfEntry";

    // The first attribute declared decides whether an entity counts as this model (IsValid, TryGetAs, All), so it
    // must be one every row has: RepositoryId is filled in only after the first fetch, so it cannot lead.

    /// <summary>
    /// The owner of the repository, such as <c>ianpatt</c>.
    /// </summary>
    public static readonly StringAttribute Organization = new(Namespace, nameof(Organization)) { IsIndexed = true };

    /// <summary>
    /// The name of the repository, such as <c>skse64</c>.
    /// </summary>
    public static readonly StringAttribute Repository = new(Namespace, nameof(Repository));

    /// <summary>
    /// The numeric id of the repository, which stays valid across renames and transfers. Recorded on the first fetch.
    /// </summary>
    public static readonly UInt64Attribute RepositoryId = new(Namespace, nameof(RepositoryId)) { IsOptional = true, IsIndexed = true };

    /// <summary>
    /// The ending of the release asset's file name to take, such as <c>.7z</c>.
    /// </summary>
    public static readonly StringAttribute AssetSuffix = new(Namespace, nameof(AssetSuffix));

    /// <summary>
    /// Text the right asset's name contains, to pick among several with the same ending.
    /// </summary>
    public static readonly StringAttribute NameHint = new(Namespace, nameof(NameHint)) { IsOptional = true };

    /// <summary>
    /// The game whose shelf lists the repository.
    /// </summary>
    public static readonly GameIdAttribute GameId = new(Namespace, nameof(GameId)) { IsIndexed = true };

    /// <summary>
    /// Whether the update check offers a prerelease as the newest version.
    /// </summary>
    public static readonly BooleanAttribute IncludePrereleases = new(Namespace, nameof(IncludePrereleases));
}

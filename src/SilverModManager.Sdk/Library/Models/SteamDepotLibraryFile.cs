using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// One file of a Steam depot manifest, fetched through the user's Steam login: a build's own copy of a game file, kept
/// as the vanilla baseline for merges and downgrade checks. The pinned hash is <see cref="LibraryFile.Sha256"/>; the
/// manifest's own digest is <see cref="Sha1"/>.
/// </summary>
[PublicAPI]
[Include<LibraryFile>]
public partial class SteamDepotLibraryFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Library.SteamDepot";

    // The first attribute declared decides whether an entity counts as this model (IsValid, TryGetAs, All), so it
    // must be one every row has.

    /// <summary>
    /// The Steam app the depot belongs to.
    /// </summary>
    public static readonly UInt32Attribute AppId = new(Namespace, nameof(AppId)) { IsIndexed = true };

    /// <summary>
    /// The depot the file is in.
    /// </summary>
    public static readonly UInt32Attribute DepotId = new(Namespace, nameof(DepotId));

    /// <summary>
    /// The manifest the file was listed in. A manifest names one build of the depot and never changes.
    /// </summary>
    public static readonly UInt64Attribute ManifestId = new(Namespace, nameof(ManifestId)) { IsIndexed = true };

    /// <summary>
    /// The file's path inside the depot, as the manifest spells it.
    /// </summary>
    public static readonly StringAttribute Path = new(Namespace, nameof(Path));

    /// <summary>
    /// The SHA-1 the manifest lists for the file, which the downloaded bytes were checked against.
    /// </summary>
    public static readonly Sha1Attribute Sha1 = new(Namespace, nameof(Sha1));
}

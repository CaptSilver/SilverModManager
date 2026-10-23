using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// A library file that was downloaded as a package zip from Thunderstore.
/// </summary>
[PublicAPI]
[Include<LibraryFile>]
public partial class ThunderstoreLibraryFile : IModelDefinition
{
    // Not called Namespace like the other models' constant: the package's team is an attribute of that name.
    private const string AttributeNamespace = "SilverModManager.Library.Thunderstore";

    // The first attribute declared decides whether an entity counts as this model (IsValid, TryGetAs, All), so it
    // must be one every row has: with the optional uuid attributes first, a package added without them would
    // not be recognised as a ThunderstoreLibraryFile.

    /// <summary>
    /// The team that owns the package, e.g. <c>Zehs</c>.
    /// </summary>
    public static readonly StringAttribute Namespace = new(AttributeNamespace, nameof(Namespace)) { IsIndexed = true };

    /// <summary>
    /// The package name, e.g. <c>REPOLib</c>.
    /// </summary>
    public static readonly StringAttribute Name = new(AttributeNamespace, nameof(Name)) { IsIndexed = true };

    /// <summary>
    /// The version as <c>Major.Minor.Patch</c>.
    /// </summary>
    public static readonly StringAttribute VersionNumber = new(AttributeNamespace, nameof(VersionNumber));

    /// <summary>
    /// The slug of the Thunderstore community the package was listed under, e.g. <c>repo</c>.
    /// </summary>
    public static readonly StringAttribute Community = new(AttributeNamespace, nameof(Community)) { IsIndexed = true };

    /// <summary>
    /// The SHA-256 the downloaded zip was verified against: the digest that was pinned beforehand, or the one
    /// computed on first download when none was. Thunderstore publishes no digest for a package zip.
    /// </summary>
    public static readonly Sha256Attribute PinnedSha256 = new(AttributeNamespace, nameof(PinnedSha256));

    /// <summary>
    /// Thunderstore's id for the package. Present when the download came from a listing that carried it.
    /// </summary>
    public static readonly StringAttribute PackageUuid = new(AttributeNamespace, nameof(PackageUuid)) { IsOptional = true };

    /// <summary>
    /// Thunderstore's id for this version of the package. Present when the download came from a listing that
    /// carried it.
    /// </summary>
    public static readonly StringAttribute VersionUuid = new(AttributeNamespace, nameof(VersionUuid)) { IsOptional = true };
}

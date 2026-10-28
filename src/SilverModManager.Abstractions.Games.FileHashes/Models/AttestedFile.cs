using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Games.FileHashes.Models;

/// <summary>
/// A vanilla file the store vouches for, recorded from the depot manifest without reading the file: the manifest's
/// SHA-1 and size, and the modified time the file had when the baseline was built. It is the store's description of
/// the path, so the index gives a file whose size and time still match it a SHA-1 identity instead of reading it.
/// <para>
/// A separate model from <see cref="HashRelation"/>, which is deduplicated by xxHash3 and needs every digest; an
/// attested file has only the SHA-1. The row lasts until a baseline rebuild replaces it or the first read of the
/// file promotes it to a <see cref="HashRelation"/> and <see cref="PathHashRelation"/>.
/// </para>
/// </summary>
public partial class AttestedFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.FileHashes.AttestedFile";

    /// <summary>
    /// The baseline of the installation the row belongs to. Two installs can share a depot manifest id, so rows are
    /// found through the baseline, which is keyed by store and install path.
    /// </summary>
    public static readonly ReferenceAttribute<LocalBaseline> Baseline = new(Namespace, nameof(Baseline)) { IsIndexed = true };

    /// <summary>
    /// The manifest row that lists the file, so a promotion appends its path row to the right depot.
    /// </summary>
    public static readonly ReferenceAttribute<SteamManifest> Manifest = new(Namespace, nameof(Manifest)) { IsIndexed = true };

    /// <summary>
    /// The path in the manifest's spelling. A disk-spelled row that differs in case would leave two game rows for
    /// one path when it meets the shipped database's row for the same file.
    /// </summary>
    public static readonly RelativePathAttribute Path = new(Namespace, nameof(Path)) { IsIndexed = true };

    /// <summary>
    /// The SHA-1 the manifest lists.
    /// </summary>
    public static readonly Sha1Attribute Sha1 = new(Namespace, nameof(Sha1));

    /// <summary>
    /// The size, which equals the file's on disk when the row was written.
    /// </summary>
    public static readonly SizeAttribute Size = new(Namespace, nameof(Size));

    /// <summary>
    /// The modified time the file had when the row was written.
    /// </summary>
    public static readonly TimestampAttribute ModifiedUtc = new(Namespace, nameof(ModifiedUtc));
}

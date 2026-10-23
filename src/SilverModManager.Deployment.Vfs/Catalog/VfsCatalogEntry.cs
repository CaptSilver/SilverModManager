using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// The hash of a file in an installation's own directory, remembered together with what <c>stat</c>
/// said about the file when it was hashed. The row is only a cache: a file whose fingerprint no
/// longer matches is hashed again and the row replaced.
/// </summary>
public partial class VfsCatalogEntry : IModelDefinition
{
    private const string Namespace = "SilverModManager.Deployment.VfsCatalogEntry";

    /// <summary>The installation the file belongs to.</summary>
    public static readonly ReferenceAttribute<GameInstallMetadata> Installation = new(Namespace, nameof(Installation)) { IsIndexed = true };

    /// <summary>The real file's absolute path.</summary>
    public static readonly StringAttribute Path = new(Namespace, nameof(Path));

    /// <summary>The device the file lived on.</summary>
    public static readonly UInt64Attribute Device = new(Namespace, nameof(Device));

    /// <summary>The file's inode. A file replaced in place keeps its path but not its inode.</summary>
    public static readonly UInt64Attribute Inode = new(Namespace, nameof(Inode));

    public static readonly SizeAttribute Size = new(Namespace, nameof(Size));

    public static readonly TimestampAttribute MTime = new(Namespace, nameof(MTime));

    public static readonly TimestampAttribute CTime = new(Namespace, nameof(CTime));

    /// <summary>The file's content hash at the time of the fingerprint.</summary>
    public static readonly HashAttribute Hash = new(Namespace, nameof(Hash));
}

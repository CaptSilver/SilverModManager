using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// Represents a <see cref="LibraryItem"/> that is a file in the library.
/// </summary>
[PublicAPI]
[Include<LibraryItem>]
public partial class LibraryFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Library.LibraryFile";

    /// <summary>
    /// Hash of the file.
    /// </summary>
    public static readonly HashAttribute Hash = new(Namespace, nameof(Hash)) { IsIndexed = true };

    /// <summary>
    /// MD5 hash for compatibility with bad systems.
    /// </summary>
    public static readonly Md5Attribute Md5 = new(Namespace, nameof(Md5)) { IsIndexed = true, IsOptional = true };

    /// <summary>
    /// SHA-256 of the file. Computed when a top-level file is added; files added earlier get it on first
    /// need. Absent means unverifiable, never a match.
    /// </summary>
    public static readonly Sha256Attribute Sha256 = new(Namespace, nameof(Sha256)) { IsIndexed = true, IsOptional = true };

    /// <summary>
    /// xxHash64 of the file, the hash Wabbajack modlists name archives by. Set for every file the library adds,
    /// archives included; rows from before it was recorded lack it. Absent means unknown, never a match.
    /// </summary>
    public static readonly HashAttribute XxHash64 = new(Namespace, nameof(XxHash64)) { IsIndexed = true, IsOptional = true };

    /// <summary>
    /// Size of the file.
    /// </summary>
    public static readonly SizeAttribute Size = new(Namespace, nameof(Size));

    /// <summary>
    /// Name of the file.
    /// </summary>
    public static readonly RelativePathAttribute FileName = new(Namespace, nameof(FileName));
}

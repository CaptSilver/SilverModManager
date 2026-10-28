using SilverModManager.Abstractions.Games.FileHashes.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Games.FileHashes.Models;

/// <summary>
/// The xxHash3 of a game file an index or baseline read in full, with the size and modified time the file had
/// before and after that read. A later index that finds the same size and time takes the hash from here instead of
/// reading the file again. Rows live in the local store, so they outlive the disk state and survive unmanaging.
/// </summary>
public partial class LocalFileHash : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.FileHashes.LocalFileHash";

    /// <summary>
    /// The installation directory, as <see cref="GameInstallMetadata.Path"/> holds it, the same key
    /// <see cref="LocalBaseline.GamePath"/> uses.
    /// </summary>
    public static readonly StringAttribute GamePath = new(Namespace, nameof(GamePath)) { IsIndexed = true };

    /// <summary>
    /// The top-level location the file is under. Two locations can hold the same relative path.
    /// </summary>
    public static readonly LocationIdAttribute Location = new(Namespace, nameof(Location));

    /// <summary>
    /// The path in the spelling the disk uses. The attribute compares case-insensitively, so a lookup has to
    /// compare the spelling itself before trusting a hit.
    /// </summary>
    public static readonly RelativePathAttribute Path = new(Namespace, nameof(Path)) { IsIndexed = true };

    /// <summary>
    /// The size of the file when it was read.
    /// </summary>
    public static readonly SizeAttribute Size = new(Namespace, nameof(Size));

    /// <summary>
    /// The modified time of the file when it was read.
    /// </summary>
    public static readonly TimestampAttribute ModifiedUtc = new(Namespace, nameof(ModifiedUtc));

    /// <summary>
    /// The xxHash3 of the file's bytes.
    /// </summary>
    public static readonly HashAttribute XxHash3 = new(Namespace, nameof(XxHash3));
}

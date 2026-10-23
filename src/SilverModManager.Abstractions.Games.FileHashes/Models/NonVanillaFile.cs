using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Abstractions.Games.FileHashes.Models;

/// <summary>
/// A file in the game directory that a <see cref="LocalBaseline"/> found not to be vanilla.
/// </summary>
public partial class NonVanillaFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.FileHashes.NonVanillaFile";

    /// <summary>
    /// The baseline that classified this file.
    /// </summary>
    public static readonly ReferenceAttribute<LocalBaseline> Baseline = new(Namespace, nameof(Baseline)) { IsIndexed = true };

    /// <summary>
    /// The path of the file, in the spelling the baseline's manifests use.
    /// </summary>
    public static readonly RelativePathAttribute Path = new(Namespace, nameof(Path)) { IsIndexed = true };

    /// <summary>
    /// Why the file is not vanilla.
    /// </summary>
    public static readonly EnumByteAttribute<NonVanillaReason> Reason = new(Namespace, nameof(Reason));

    /// <summary>
    /// The size of the file on disk.
    /// </summary>
    public static readonly SizeAttribute Size = new(Namespace, nameof(Size));
}

/// <summary>
/// How a file differs from the vanilla file list.
/// </summary>
public enum NonVanillaReason : byte
{
    /// <summary>The manifests do not list the file.</summary>
    NotInManifest = 0,

    /// <summary>The manifests list the file with a different size.</summary>
    SizeDiffers = 1,

    /// <summary>The manifests list the file with the same size and a different hash.</summary>
    HashDiffers = 2,
}

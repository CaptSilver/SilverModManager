using SilverModManager.Sdk.Hashes;
using OneOf;

namespace SilverModManager.Abstractions.GuidedInstallers;

/// <summary>
/// An image.
/// </summary>
public sealed class OptionImage : OneOfBase<Uri, OptionImage.ImageStoredFile>
{
    /// <summary>
    /// Represents an image from an archive.
    /// </summary>
    public record struct ImageStoredFile(FileIdentity Identity);

    /// <inheritdoc />
    public OptionImage(OneOf<Uri, ImageStoredFile> input) : base(input) { }
}

using System.Text.Json;
using NexusMods.Paths;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Transforms;

namespace NexusMods.Wabbajack.Install;

/// <summary>
/// The transforms this build has: Octodiff patching and texture re-encoding. Archive building says it is not there,
/// so the directives that need it become skip rows instead of failing the install.
/// </summary>
public sealed class DirectiveTransforms : IDirectiveTransforms
{
    public Task Patch(Stream basis, Stream delta, Stream output, CancellationToken cancellationToken)
        => OctodiffPatch.Apply(basis, delta, output, cancellationToken);

    public Task Recompress(Stream dds, ImageState target, Stream output, CancellationToken cancellationToken)
        => DdsRecompressor.Recompress(dds, ToTarget(target), output, cancellationToken);

    /// <summary>The list names the format as a DXGI_FORMAT; a name this build does not know cannot be written either.</summary>
    private static DdsTarget ToTarget(ImageState image)
    {
        if (!Enum.TryParse<DxgiFormat>(image.Format, ignoreCase: false, out var format) || !Enum.IsDefined(format))
            throw new UnsupportedTextureFormatException($"Cannot re-encode a texture with format {image.Format}");
        return new DdsTarget(image.Width, image.Height, image.MipLevels, format);
    }

    public Task BuildBsa(JsonElement state, JsonElement[] fileStates, Func<RelativePath, Task<Stream>> openInput, Stream output, CancellationToken cancellationToken)
        => throw new TransformUnavailableException("build archives");
}

using System.Text.Json;
using NexusMods.Paths;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Transforms;

namespace NexusMods.Wabbajack.Install;

/// <summary>
/// The transforms this build has: Octodiff patching. Texture recompression and archive building say they are not
/// there, so the directives that need them become skip rows instead of failing the install.
/// </summary>
public sealed class DirectiveTransforms : IDirectiveTransforms
{
    public Task Patch(Stream basis, Stream delta, Stream output, CancellationToken cancellationToken)
        => OctodiffPatch.Apply(basis, delta, output, cancellationToken);

    public Task Recompress(Stream dds, ImageState target, Stream output, CancellationToken cancellationToken)
        => throw new TransformUnavailableException("recompress textures");

    public Task BuildBsa(JsonElement state, JsonElement[] fileStates, Func<RelativePath, Task<Stream>> openInput, Stream output, CancellationToken cancellationToken)
        => throw new TransformUnavailableException("build archives");
}

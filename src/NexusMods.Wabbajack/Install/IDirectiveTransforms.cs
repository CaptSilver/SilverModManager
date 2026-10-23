using System.Text.Json;
using NexusMods.Paths;
using NexusMods.Wabbajack.Format;

namespace NexusMods.Wabbajack.Install;

/// <summary>
/// The directives that make a file out of other bytes: patching, recompressing a texture and building a Bethesda
/// archive. Kept behind an interface so the install does not depend on the codecs.
/// </summary>
public interface IDirectiveTransforms
{
    Task Patch(Stream basis, Stream delta, Stream output, CancellationToken cancellationToken);

    Task Recompress(Stream dds, ImageState target, Stream output, CancellationToken cancellationToken);

    Task BuildBsa(JsonElement state, JsonElement[] fileStates, Func<RelativePath, Task<Stream>> openInput, Stream output, CancellationToken cancellationToken);
}

/// <summary>Thrown by a transform this build does not have; the directive that needed it becomes a skip row.</summary>
public sealed class TransformUnavailableException(string kind) : Exception($"this build cannot {kind} yet")
{
    public string Kind { get; } = kind;
}

using System.Text.Json;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Install;

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

/// <summary>
/// Thrown when an archive that was just built does not hold the bytes its inputs were written with. The message names
/// the entry, so the skip row for the archive says which one.
/// </summary>
public sealed class ArchiveMismatchException(string entry, string problem) : Exception(problem)
{
    public string Entry { get; } = entry;
}

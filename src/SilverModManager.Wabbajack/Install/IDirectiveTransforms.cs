using System.Text.Json;
using NexusMods.Paths;
using SilverModManager.Games.Bethesda.Archives;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// The directives that make a file out of other bytes: patching, recompressing a texture and building a Bethesda
/// archive. Kept behind an interface so the install does not depend on the codecs.
/// </summary>
public interface IDirectiveTransforms
{
    Task Patch(Stream basis, Stream delta, Stream output, CancellationToken cancellationToken);

    /// <summary>
    /// <paramref name="parallelism"/> is how many threads the encoder may spread one texture over: 1 when the caller
    /// already runs one texture per worker, the worker count when it feeds them one at a time.
    /// </summary>
    Task Recompress(Stream dds, ImageState target, Stream output, int parallelism, CancellationToken cancellationToken);

    /// <summary>
    /// <paramref name="openInput"/> gets the parsed state of the file it opens next to its path, so the caller can treat
    /// a kind of archive entry differently from another.
    /// </summary>
    Task BuildBsa(JsonElement state, JsonElement[] fileStates, Func<RelativePath, FileState, Task<Stream>> openInput, Stream output, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when an archive that was just built does not hold the bytes its inputs were written with. The message names
/// the entry, so the skip row for the archive says which one.
/// </summary>
public sealed class ArchiveMismatchException(string entry, string problem) : Exception(problem)
{
    public string Entry { get; } = entry;
}

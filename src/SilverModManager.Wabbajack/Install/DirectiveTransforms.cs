using System.Text.Json;
using SilverModManager.Games.Bethesda.Archives;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// The transforms this build has: Octodiff patching, texture re-encoding and Bethesda archive building.
/// </summary>
public sealed class DirectiveTransforms(TemporaryFileManager temporaryFiles, DdsRecompressor recompressor) : IDirectiveTransforms
{
    /// <summary>The transforms over a recompressor that uses BCnEncoder alone, for tests and tools that have no service container.</summary>
    public static DirectiveTransforms Default(TemporaryFileManager temporaryFiles) => new(temporaryFiles, DdsRecompressor.Managed());

    public async Task Patch(Stream basis, Stream delta, Stream output, CancellationToken cancellationToken)
    {
        if (basis.CanSeek && delta.CanSeek)
        {
            await OctodiffPatch.Apply(basis, delta, output, cancellationToken);
            return;
        }

        // A stream that cannot seek is spooled to disk, in the app's temp folder rather than the system's, which is memory on many machines.
        await using var spool = temporaryFiles.CreateFolder();
        await OctodiffPatch.Apply(basis, delta, output, cancellationToken, spool.Path.ToString());
    }

    public Task Recompress(Stream dds, ImageState target, Stream output, int parallelism, CancellationToken cancellationToken)
        => recompressor.Recompress(dds, ToTarget(target), output, parallelism, cancellationToken);

    /// <summary>The list names the format as a DXGI_FORMAT; a name this build does not know cannot be written either.
    /// <c>UNKNOWN</c> passes through: the recompressor resolves it to the source texture's format.</summary>
    private static DdsTarget ToTarget(ImageState image)
    {
        if (!Enum.TryParse<DxgiFormat>(image.Format, ignoreCase: false, out var format) || !Enum.IsDefined(format))
            throw new UnsupportedTextureFormatException($"Cannot re-encode a texture with format {image.Format}");
        if (image.Width < 1 || image.Height < 1)
            throw new UnsupportedTextureFormatException($"cannot write: the list records a {image.Width}x{image.Height} texture");
        return new DdsTarget(image.Width, image.Height, image.MipLevels, format);
    }

    /// <summary>
    /// Builds the archive and reads it back: the inputs are hashed as the builder takes them, so a mistake in the
    /// writer shows up as an <see cref="ArchiveMismatchException"/> instead of a broken archive in the user's mod folder.
    /// <paramref name="output"/> has to be readable and seekable for that.
    /// </summary>
    public async Task BuildBsa(JsonElement state, JsonElement[] fileStates, Func<RelativePath, FileState, Task<Stream>> openInput, Stream output, CancellationToken cancellationToken)
    {
        if (output is not { CanRead: true, CanSeek: true })
            throw new ArgumentException("An archive is checked by reading it back, so the stream it is written to must be readable and seekable", nameof(output));

        var archiveState = ArchiveStateJson.ParseArchiveState(state);
        var written = new Dictionary<string, FileIdentity>();
        await using (var builder = BethesdaArchiveBuilder.Create(archiveState, temporaryFiles))
        {
            foreach (var element in fileStates)
            {
                var fileState = ArchiveStateJson.ParseFileState(element);
                await using var input = await openInput(fileState.Path, fileState);
                using var hashing = new Xx64HashingStream(input);
                await builder.AddFile(fileState, hashing, cancellationToken);
                written[BuiltArchiveCheck.Fold(fileState.Path.ToString())] = hashing.Hash;
            }

            await builder.Build(output, cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
        await BuiltArchiveCheck.Verify(output, written, cancellationToken);
        output.Seek(0, SeekOrigin.End);
    }
}

using System.Text.Json;
using NexusMods.Paths;
using NexusMods.Wabbajack.Install;

namespace NexusMods.Wabbajack.Tests.Install;

/// <summary>
/// Transforms that need no codec: a patch is the basis followed by the delta, and an archive is its inputs end to end
/// in the order the file states name them. A recompressed texture is the source plus one byte, so it never hashes to
/// what the list recorded, as a real re-encode often does not. Enough to see the job route bytes through them and
/// check the result.
/// </summary>
internal sealed class ConcatenatingTransforms : IDirectiveTransforms
{
    public async Task Patch(Stream basis, Stream delta, Stream output, CancellationToken cancellationToken)
    {
        await basis.CopyToAsync(output, cancellationToken);
        await delta.CopyToAsync(output, cancellationToken);
    }

    public async Task Recompress(Stream dds, NexusMods.Wabbajack.Format.ImageState target, Stream output, CancellationToken cancellationToken)
    {
        await dds.CopyToAsync(output, cancellationToken);
        output.WriteByte(0xFF);
    }

    public async Task BuildBsa(JsonElement state, JsonElement[] fileStates, Func<RelativePath, Task<Stream>> openInput, Stream output, CancellationToken cancellationToken)
    {
        foreach (var fileState in fileStates)
        {
            var path = RelativePath.FromUnsanitizedInput(fileState.GetProperty("Path").GetString()!);
            await using var input = await openInput(path);
            await input.CopyToAsync(output, cancellationToken);
        }
    }
}

/// <summary>
/// A patch that fails with an error the job does not turn into a skip row, and a texture encode that only ends when
/// its token is cancelled: what an install that dies mid-batch with slow encodes still queued looks like.
/// </summary>
internal sealed class FailingPatchSlowTextureTransforms : IDirectiveTransforms
{
    public Task Patch(Stream basis, Stream delta, Stream output, CancellationToken cancellationToken)
        => throw new InvalidOperationException("the patch tool crashed");

    public async Task Recompress(Stream dds, NexusMods.Wabbajack.Format.ImageState target, Stream output, CancellationToken cancellationToken)
        => await Task.Delay(Timeout.Infinite, cancellationToken);

    public Task BuildBsa(JsonElement state, JsonElement[] fileStates, Func<RelativePath, Task<Stream>> openInput, Stream output, CancellationToken cancellationToken)
        => throw new InvalidOperationException("no archive is built here");
}

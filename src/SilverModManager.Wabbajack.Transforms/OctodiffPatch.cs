// Follows Wabbajack.Installer/Utilities/BinaryPatching.cs.
using Octodiff.Core;
using Octodiff.Diagnostics;

namespace NexusMods.Wabbajack.Transforms;

/// <summary>
/// Applies Octodiff binary deltas, the format Wabbajack uses for patched files.
/// </summary>
public static class OctodiffPatch
{
    /// <summary>
    /// Writes <paramref name="basis"/> patched with <paramref name="delta"/> to <paramref name="output"/>.
    /// Octodiff seeks in both the basis and the delta, so either one that cannot seek is spooled to a
    /// temp file first.
    /// <paramref name="output"/> must be readable and seekable: Octodiff re-reads it to check the result's hash.
    /// </summary>
    /// <exception cref="PatchException">The delta is corrupt or does not fit the basis.</exception>
    public static async Task Apply(Stream basis, Stream delta, Stream output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        TempFile? basisSpool = null;
        TempFile? deltaSpool = null;
        try
        {
            if (!basis.CanSeek) basisSpool = await TempFile.CopyOf(basis, token);
            if (!delta.CanSeek) deltaSpool = await TempFile.CopyOf(delta, token);

            token.ThrowIfCancellationRequested();
            ApplySeekable(basisSpool?.Stream ?? basis, deltaSpool?.Stream ?? delta, output, token);
        }
        finally
        {
            basisSpool?.Dispose();
            deltaSpool?.Dispose();
        }
    }

    private static void ApplySeekable(Stream basis, Stream delta, Stream output, CancellationToken token)
    {
        // Octodiff has no cancellation hook, but it writes as it goes, so a wrapper on the output
        // is where a cancelled token gets noticed.
        var checkedOutput = new CancellableWriteStream(output, token);
        try
        {
            new DeltaApplier().Apply(basis, new BinaryDeltaReader(new DeltaReadStream(delta, token), new NullProgressReporter()), checkedOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not PatchException)
        {
            throw new PatchException($"Applying the delta failed: {ex.Message}", ex);
        }
    }
}

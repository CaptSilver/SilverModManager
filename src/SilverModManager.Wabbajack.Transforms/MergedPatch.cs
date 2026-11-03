// Follows the merged-patch branch of Wabbajack.Installer/StandardInstaller.cs.
namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// Builds a zEdit merged patch: the sources' bytes laid end to end are the basis of an Octodiff delta.
/// </summary>
public static class MergedPatch
{
    /// <summary>
    /// Concatenates <paramref name="sources"/> in the order given, then applies <paramref name="delta"/> to that.
    /// The concatenation is spooled to <paramref name="spoolDirectory"/>, the system temp folder when null.
    /// </summary>
    /// <exception cref="PatchException">The delta is corrupt or does not fit the concatenated sources.</exception>
    public static async Task Apply(IReadOnlyList<Stream> sources, Stream delta, Stream output, CancellationToken token, string? spoolDirectory = null)
    {
        var spool = spoolDirectory ?? Path.GetTempPath();
        using var basis = await TempFile.Concatenation(sources, spool, token);
        await OctodiffPatch.Apply(basis.Stream, delta, output, token, spool);
    }
}

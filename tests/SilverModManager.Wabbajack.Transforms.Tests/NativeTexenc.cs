namespace SilverModManager.Wabbajack.Transforms.Tests;

/// <summary>
/// Classes that load the real libsmmtexenc.so share one xunit collection, so they never run at once: its init fills codec
/// tables without a lock and every class that loads the library calls it.
/// </summary>
internal static class NativeTexenc
{
    public const string Collection = "native texenc";
}

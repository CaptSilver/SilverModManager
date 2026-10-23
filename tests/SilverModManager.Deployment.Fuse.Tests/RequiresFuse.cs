namespace NexusMods.Deployment.Fuse.Tests;

/// <summary>
/// Skips a test where FUSE is not installed. Tests that mount carry <c>[Trait("RequiresFuse", "True")]</c>
/// by hand and call <see cref="SkipUnlessAvailable"/> first.
/// </summary>
/// <remarks>
/// Only environmental facts skip: no <c>/dev/fuse</c>, no <c>fusermount3</c>, no libfuse3 file. A library
/// that is installed but fails to load, or a mount refused with all three present, is a real failure and
/// must stay red, otherwise a broken mount would hide behind a skip.
/// </remarks>
public static class RequiresFuse
{
    private static readonly string[] LibraryDirectories = ["/usr/lib64", "/lib64"];

    public static bool IsLibraryInstalled() => LibraryDirectories
        .Where(Directory.Exists)
        .Any(directory => Directory.EnumerateFiles(directory, "libfuse3.so.*").Any());

    public static void SkipUnlessLibraryInstalled()
    {
        Assert.SkipUnless(IsLibraryInstalled(), "libfuse3 is not installed");
    }

    public static void SkipUnlessAvailable()
    {
        Assert.SkipUnless(File.Exists("/dev/fuse"), "/dev/fuse does not exist");
        Assert.SkipUnless(IsOnPath("fusermount3"), "fusermount3 is not on PATH");
        SkipUnlessLibraryInstalled();
    }

    private static bool IsOnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(':', StringSplitOptions.RemoveEmptyEntries)
        .Any(directory => File.Exists(Path.Combine(directory, executable)));
}

using FluentAssertions;
using NexusMods.Backend.Games;
using NexusMods.Paths;

namespace NexusMods.Backend.Tests.Games;

/// <summary>
/// An install folder is the same folder however its path is spelled. Steam writes the home library as
/// /var/home/user/... where $HOME says /home/user/..., and /home is a symlink to /var/home.
/// </summary>
public class InstallPathsTests
{
    private static readonly InMemoryFileSystem FileSystem = new(OSInformation.FakeUnix);

    private static string? FakeReadLink(string path) => path switch
    {
        "/home" => "/var/home",
        "/mnt/games" => "/run/media/games",
        _ => null,
    };

    [Theory]
    [InlineData("/var/home/user/lib/game", "/home/user/lib/game")]
    [InlineData("/home/user/lib/game", "/var/home/user/lib/game")]
    [InlineData("/mnt/games/steam/game", "/run/media/games/steam/game")]
    [InlineData("/home/user/lib/game", "/home/user/lib/game")]
    public void AreSameFolder_SpelledThroughASymlink_IsTrue(string stored, string actual)
    {
        InstallPaths.AreSameFolder(stored, FileSystem.FromUnsanitizedFullPath(actual), FakeReadLink).Should().BeTrue();
    }

    [Fact]
    public void AreSameFolder_DifferentFolders_IsFalse()
    {
        InstallPaths.AreSameFolder("/home/user/lib/game", FileSystem.FromUnsanitizedFullPath("/home/user/lib/other"), FakeReadLink).Should().BeFalse();
    }

    [Fact]
    public void AreSameFolder_RelativeSymlinkTarget_IsResolvedAgainstItsParent()
    {
        string? readLink(string path) => path == "/library/current" ? "versions/2" : null;

        InstallPaths.AreSameFolder("/library/current/game", FileSystem.FromUnsanitizedFullPath("/library/versions/2/game"), readLink).Should().BeTrue();
    }

    [Fact]
    public void AreSameFolder_SymlinkLoop_IsFalseInsteadOfHanging()
    {
        string? readLink(string path) => path switch { "/a" => "/b", "/b" => "/a", _ => null };

        InstallPaths.AreSameFolder("/a/game", FileSystem.FromUnsanitizedFullPath("/c/game"), readLink).Should().BeFalse();
    }

    [Fact]
    public void AreSameFolder_ReadingALinkFails_FallsBackToTheSpelledPath()
    {
        string? readLink(string path) => throw new IOException("No such device");

        InstallPaths.AreSameFolder("/mnt/usb/game", FileSystem.FromUnsanitizedFullPath("/mnt/usb/game"), readLink).Should().BeTrue();
        InstallPaths.AreSameFolder("/mnt/usb/game", FileSystem.FromUnsanitizedFullPath("/mnt/other/game"), readLink).Should().BeFalse();
    }

    [Fact]
    public void AreSameFolder_RealSymlinkOnDisk_IsResolved()
    {
        var root = Directory.CreateTempSubdirectory("install-paths");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real", "game"));
            var link = Path.Combine(root.FullName, "link");
            Directory.CreateSymbolicLink(link, Path.Combine(root.FullName, "real"));

            InstallPaths.AreSameFolder(Path.Combine(link, "game"), FileSystem.FromUnsanitizedFullPath(real.FullName)).Should().BeTrue();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}

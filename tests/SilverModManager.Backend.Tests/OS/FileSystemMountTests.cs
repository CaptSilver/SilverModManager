using FluentAssertions;
using NexusMods.Backend.OS;
using NexusMods.Paths;

namespace NexusMods.Backend.Tests.OS;

public class FileSystemMountTests
{
    // df fails on a path that doesn't exist, and on a first run the archive folder hasn't been
    // created yet. A folder lands on the same mount as its parent, so df is asked about that.
    [Fact]
    public void Test_MissingFolderResolvesToNearestExistingParent()
    {
        var fs = new InMemoryFileSystem();
        var data = fs.FromUnsanitizedFullPath("/home/user/.local/share");
        fs.CreateDirectory(data);

        var archives = data / "SilverModManager" / "DataModel" / "Archives";

        LinuxInterop.NearestExistingDirectory(archives).Should().Be(data);
    }

    [Fact]
    public void Test_ExistingFolderResolvesToItself()
    {
        var fs = new InMemoryFileSystem();
        var archives = fs.FromUnsanitizedFullPath("/data/Archives");
        fs.CreateDirectory(archives);

        LinuxInterop.NearestExistingDirectory(archives).Should().Be(archives);
    }

    [Fact]
    public void Test_NothingExistsResolvesToRoot()
    {
        var fs = new InMemoryFileSystem();
        var path = fs.FromUnsanitizedFullPath("/nothing/here");

        LinuxInterop.NearestExistingDirectory(path).Should().Be(fs.FromUnsanitizedFullPath("/"));
    }
}

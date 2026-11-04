using FluentAssertions;
using SilverModManager.Backend.FileExtractor.Extractors;

namespace SilverModManager.Backend.Tests.FileExtractor;

public class RawFolderWalkTests
{
    [Theory]
    [InlineData(95, true)] // ENOTSUP: glibc before 2.32 refuses AT_SYMLINK_NOFOLLOW outright
    [InlineData(38, true)] // ENOSYS
    [InlineData(13, false)] // EACCES is a real failure
    [InlineData(2, false)]
    public void OnlyUnsupportedErrorsFallBackToThePlainChmod(int errno, bool fallsBack) =>
        RawFolderWalk.NoFollowUnsupported(errno).Should().Be(fallsBack);

    [Fact]
    public void ChildrenListsNamesThatAreNotUtf8WithTheirKind()
    {
        var root = Directory.CreateTempSubdirectory("raw-walk-").FullName;
        byte[] file = [(byte)'f', 0xFF];
        byte[] folder = [(byte)'d', 0xFE];
        byte[] link = [(byte)'l', 0xFD];
        RawSymlink.CreateFile(root, file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        RawSymlink.CreateFolder(root, folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RawSymlink.Create(root, link, "/nonexistent");

        try
        {
            var kinds = RawFolderWalk.Children(RawFolderWalk.Terminated(root)).ToDictionary(e => e.Path.SequenceEqual(RawSymlink.RawPath(root, file)) ? "file" : e.Path.SequenceEqual(RawSymlink.RawPath(root, folder)) ? "folder" : "link", e => e.Kind);
            kinds.Should().Equal(new Dictionary<string, RawStat.Kind> { ["file"] = RawStat.Kind.Other, ["folder"] = RawStat.Kind.Folder, ["link"] = RawStat.Kind.Link });
        }
        finally
        {
            foreach (var name in new[] { file, folder, link }) RawSymlink.Remove(root, name);
            Directory.Delete(root);
        }
    }
}

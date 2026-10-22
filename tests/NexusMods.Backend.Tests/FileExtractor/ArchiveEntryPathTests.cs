using FluentAssertions;
using NexusMods.Backend.FileExtractor;

namespace NexusMods.Backend.Tests.FileExtractor;

public class ArchiveEntryPathTests
{
    [Theory]
    [InlineData("x.txt", "x.txt")]
    [InlineData("a/b/x.txt", "a/b/x.txt")]
    [InlineData("a/b/", "a/b")]
    [InlineData("sub/../x.txt", "x.txt")]
    [InlineData("../x.txt", "x.txt")]
    [InlineData("../../x.txt", "x.txt")]
    [InlineData("sub/../../x.txt", "x.txt")]
    [InlineData("a/b/../../../c", "c")]
    [InlineData("a/../b/../c/d", "c/d")]
    [InlineData("./x.txt", "x.txt")]
    [InlineData("a/./b/./x.txt", "a/b/x.txt")]
    [InlineData("/x.txt", "x.txt")]
    [InlineData("///a//b///x.txt", "a/b/x.txt")]
    [InlineData("C:\\x.txt", "x.txt")]
    [InlineData("c:/dir/x.txt", "dir/x.txt")]
    [InlineData("C:x.txt", "x.txt")]
    [InlineData("back\\slash\\x.txt", "back/slash/x.txt")]
    [InlineData("..\\..\\x.txt", "x.txt")]
    [InlineData("a\\..\\..\\x.txt", "x.txt")]
    [InlineData("\\\\server\\share\\x.txt", "server/share/x.txt")]
    [InlineData("mods/naïve/ファイル.txt", "mods/naïve/ファイル.txt")]
    [InlineData("a/C:/x.txt", "a/C:/x.txt")]
    [InlineData("a/...b/x.txt", "a/...b/x.txt")]
    public void CleanResolvesTheNameAgainstTheArchiveRoot(string raw, string expected)
    {
        ArchiveEntryPath.Clean(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("C:\\")]
    [InlineData("a/..")]
    [InlineData("")]
    public void ANameThatResolvesToNothingCleansToEmpty(string raw)
    {
        ArchiveEntryPath.Clean(raw).Should().BeEmpty();
    }

    [Fact]
    public void ASegmentWithANulIsDropped()
    {
        ArchiveEntryPath.Clean("a/b\0c/x.txt").Should().Be("a/x.txt");
        ArchiveEntryPath.Clean("\0").Should().BeEmpty();
    }

    [Theory]
    [InlineData("a/b.txt", "a/b.txt", false)]
    [InlineData("a/b/", "a/b", false)]
    [InlineData("../b.txt", "b.txt", true)]
    [InlineData("a\\b.txt", "a/b.txt", true)]
    [InlineData("/b.txt", "b.txt", true)]
    [InlineData("C:/b.txt", "b.txt", true)]
    public void IsRewrittenIgnoresTheTrailingSeparatorOfAFolderEntry(string raw, string cleaned, bool expected)
    {
        ArchiveEntryPath.IsRewritten(raw, cleaned).Should().Be(expected);
    }

    // `tar -C dir .` stores every entry as "./...", and nothing about where those files land is surprising.
    [Theory]
    [InlineData("./b.txt")]
    [InlineData("./a/b.txt")]
    [InlineData("a//b.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("./a/")]
    public void DotSegmentsAndDoubledSeparatorsAreNotARewrite(string raw)
    {
        ArchiveEntryPath.IsRewritten(raw, ArchiveEntryPath.Clean(raw)).Should().BeFalse();
    }
}

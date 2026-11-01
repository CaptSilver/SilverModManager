using System.Text;
using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>text/uri-list as a drag source sends it, reduced to the local paths the window can open.</summary>
public class UriListParserTests
{
    private readonly UriListParser _parser = new();

    private UriListParse Parse(string text) => _parser.Parse(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void EmptyInputGivesNothing()
    {
        var parse = _parser.Parse([]);

        parse.LocalPaths.Should().BeEmpty();
        parse.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void LinesSplitOnCrLf()
    {
        Parse("file:///home/u/a.zip\r\nfile:///home/u/b.zip\r\n").LocalPaths
            .Should().Equal("/home/u/a.zip", "/home/u/b.zip");
    }

    [Fact]
    public void LinesSplitOnBareLf()
    {
        Parse("file:///home/u/a.zip\nfile:///home/u/b.zip").LocalPaths
            .Should().Equal("/home/u/a.zip", "/home/u/b.zip");
    }

    [Fact]
    public void CommentAndBlankLinesAreDroppedSilently()
    {
        var parse = Parse("# a comment\r\n\r\nfile:///home/u/a.zip\r\n   \r\n");

        parse.LocalPaths.Should().Equal("/home/u/a.zip");
        parse.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void PercentEscapesAreDecoded()
    {
        Parse("file:///home/u/a%20b.zip").LocalPaths.Should().Equal("/home/u/a b.zip");
    }

    [Fact]
    public void EscapedHashAndPercentDecodeToThemselves()
    {
        Parse("file:///home/u/a%23b%25c.zip").LocalPaths.Should().Equal("/home/u/a#b%c.zip");
    }

    [Fact]
    public void MultiByteUtf8EscapesDecodeToOneCharacter()
    {
        Parse("file:///home/u/caf%C3%A9.zip").LocalPaths.Should().Equal("/home/u/café.zip");
    }

    [Fact]
    public void LocalhostAuthorityIsAccepted()
    {
        Parse("file://localhost/x/a.zip").LocalPaths.Should().Equal("/x/a.zip");
    }

    [Fact]
    public void SchemeIsMatchedCaseInsensitively()
    {
        Parse("FILE:///x/a.zip").LocalPaths.Should().Equal("/x/a.zip");
    }

    [Fact]
    public void AnotherMachineIsSkippedWithAReason()
    {
        var parse = Parse("file://nas/x/a.zip");

        parse.LocalPaths.Should().BeEmpty();
        parse.Skipped.Should().Equal(new UriListSkip("file://nas/x/a.zip", "on another machine"));
    }

    [Theory]
    [InlineData("trash:/a.zip")]
    [InlineData("smb://nas/share/a.zip")]
    [InlineData("remote:/a.zip")]
    public void SchemesThatAreNotLocalFilesAreSkippedWithAReason(string uri)
    {
        var parse = Parse(uri);

        parse.LocalPaths.Should().BeEmpty();
        parse.Skipped.Should().Equal(new UriListSkip(uri, "not a local file"));
    }

    [Fact]
    public void ANameThatIsNotUtf8IsSkippedAndTheNextLineStillParses()
    {
        var bytes = Encoding.ASCII.GetBytes("file:///home/u/bad%FFname.zip\r\nfile:///home/u/ok.zip\r\n");

        var parse = _parser.Parse(bytes);

        parse.LocalPaths.Should().Equal("/home/u/ok.zip");
        parse.Skipped.Should().Equal(new UriListSkip("file:///home/u/bad%FFname.zip", "name is not valid UTF-8"));
    }

    [Fact]
    public void ARawFFByteInTheUriIsSkippedAsNotUtf8()
    {
        byte[] bytes = [.. Encoding.ASCII.GetBytes("file:///home/u/bad"), 0xFF, .. Encoding.ASCII.GetBytes(".zip\r\nfile:///home/u/ok.zip")];

        var parse = _parser.Parse(bytes);

        parse.LocalPaths.Should().Equal("/home/u/ok.zip");
        parse.Skipped.Should().ContainSingle().Which.Reason.Should().Be("name is not valid UTF-8");
    }

    [Fact]
    public void ARawSpaceInThePathIsAccepted()
    {
        Parse("file:///home/u/a b.zip").LocalPaths.Should().Equal("/home/u/a b.zip");
    }

    [Fact]
    public void ARawTrailingSpaceBelongsToTheName()
    {
        Parse("file:///home/u/a.zip \r\n").LocalPaths.Should().Equal("/home/u/a.zip ");
    }

    [Fact]
    public void ARawTrailingTabBelongsToTheName()
    {
        Parse("file:///home/u/a.zip\t\n").LocalPaths.Should().Equal("/home/u/a.zip\t");
    }

    [Fact]
    public void ALineWithLeadingSpaceIsSkippedInsteadOfRepathed()
    {
        var parse = Parse(" file:///home/u/a.zip\n");

        parse.LocalPaths.Should().BeEmpty();
        parse.Skipped.Should().ContainSingle().Which.Reason.Should().Be("not a local file");
    }

    [Fact]
    public void ALineOfOnlyWhitespaceIsBlank()
    {
        var parse = Parse(" \t \r\nfile:///home/u/a.zip\r\n");

        parse.LocalPaths.Should().Equal("/home/u/a.zip");
        parse.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void ATrailingSlashIsKeptOffSoFoldersLookLikeFiles()
    {
        Parse("file:///home/u/folder/").LocalPaths.Should().Equal("/home/u/folder");
    }

    [Fact]
    public void RootIsNotTrimmedToNothing()
    {
        Parse("file:///").LocalPaths.Should().Equal("/");
    }

    [Fact]
    public void OrderIsInputOrderAndDuplicatesAreKept()
    {
        Parse("file:///b\r\nfile:///a\r\nfile:///b").LocalPaths.Should().Equal("/b", "/a", "/b");
    }

    [Fact]
    public void AnEscapedNulByteIsSkippedBecauseNoPathCanHoldIt()
    {
        var parse = Parse("file:///home/u/a%00b.zip");

        parse.LocalPaths.Should().BeEmpty();
        parse.Skipped.Should().ContainSingle().Which.Reason.Should().Be("name contains a NUL byte");
    }

    [Fact]
    public void AUriWithoutAnAuthorityIsReadAsAPath()
    {
        Parse("file:/home/u/a.zip").LocalPaths.Should().Equal("/home/u/a.zip");
    }
}

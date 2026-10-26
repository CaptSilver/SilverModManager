using FluentAssertions;
using SilverModManager.App.UI.Pages.Ini;

namespace SilverModManager.UI.Tests;

public class IniDiffTests
{
    private static string[] Render(string before, string after) =>
        IniDiff.Lines(before, after).Select(l => $"{(l.Kind == IniDiffKind.Added ? '+' : l.Kind == IniDiffKind.Removed ? '-' : ' ')}{l.Text}").ToArray();

    [Fact]
    public void IdenticalTextHasOnlyUnchangedLines()
    {
        Render("a\r\nb\r\n", "a\r\nb\r\n").Should().Equal(" a", " b");
    }

    [Fact]
    public void AChangedLineIsARemovalFollowedByAnAddition()
    {
        Render("[S]\r\nk=1\r\nz=9\r\n", "[S]\r\nk=2\r\nz=9\r\n").Should().Equal(" [S]", "-k=1", "+k=2", " z=9");
    }

    [Fact]
    public void AppendedAndDeletedLinesAreFound()
    {
        Render("a\nb\n", "a\nb\nc\n").Should().Equal(" a", " b", "+c");
        Render("a\nb\nc\n", "a\nc\n").Should().Equal(" a", "-b", " c");
    }

    [Fact]
    public void LineEndingsDoNotCountAsChanges()
    {
        Render("a\r\nb\r\n", "a\nb\n").Should().Equal(" a", " b");
    }

    [Fact]
    public void EmptyTextIsNoLines()
    {
        Render("", "").Should().BeEmpty();
        Render("", "a\n").Should().Equal("+a");
    }
}

using FluentAssertions;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;

namespace SilverModManager.Games.Bethesda.LoadOrder.Tests.Formats;

public class MorrowindIniGameFilesTests
{
    private static string Join(string eol, params string[] lines) => string.Concat(lines.Select(l => l + eol));

    private static readonly string[] Ini =
    [
        "[General]",
        "Screen Width=1920",
        "",
        "[Game Files]",
        "GameFile0=Morrowind.esm",
        "GameFile1=Old.esp",
        "",
        "[Weather]",
        "Sunrise Time=6",
    ];

    [Fact]
    public void ParseReadsTheGameFileLinesInNumericOrder()
    {
        var text = Join("\n", "[Game Files]", "GameFile10=Ten.esp", "GameFile2=Two.esp", "GameFile0=Morrowind.esm");

        MorrowindIniGameFiles.Parse(text).Should().Equal("Morrowind.esm", "Two.esp", "Ten.esp");
    }

    [Fact]
    public void ParseIgnoresGameFileKeysOutsideTheSection()
    {
        var text = Join("\r\n", "[General]", "GameFile0=Wrong.esp", "[game files]", "gamefile0 = Right.esp ", "[Other]", "GameFile1=Wrong2.esp");

        MorrowindIniGameFiles.Parse(text).Should().Equal("Right.esp");
    }

    [Fact]
    public void ParseOfAFileWithoutTheSectionIsEmpty() =>
        MorrowindIniGameFiles.Parse(Join("\n", "[General]", "a=b")).Should().BeEmpty();

    [Fact]
    public void RewriteReplacesTheSectionAndKeepsEveryOtherLine()
    {
        var result = MorrowindIniGameFiles.Rewrite(Join("\n", Ini), ["Morrowind.esm", "Mod.esp"]);

        result.Should().Be(Join("\n",
            "[General]",
            "Screen Width=1920",
            "",
            "[Game Files]",
            "GameFile0=Morrowind.esm",
            "GameFile1=Mod.esp",
            "",
            "[Weather]",
            "Sunrise Time=6"));
    }

    [Fact]
    public void RewriteKeepsCrLfLineEndings()
    {
        var result = MorrowindIniGameFiles.Rewrite(Join("\r\n", Ini), ["A.esp"]);

        result.Replace("\r\n", "").Should().NotContain("\n");
        result.Should().Contain("[Game Files]\r\nGameFile0=A.esp\r\n\r\n[Weather]");
    }

    [Fact]
    public void RewriteWithNoNamesLeavesAnEmptySection()
    {
        var result = MorrowindIniGameFiles.Rewrite(Join("\n", Ini), []);

        result.Should().Be(Join("\n", "[General]", "Screen Width=1920", "", "[Game Files]", "", "[Weather]", "Sunrise Time=6"));
    }

    [Fact]
    public void RewriteCreatesTheSectionAtTheEndWhenMissing()
    {
        var result = MorrowindIniGameFiles.Rewrite("[General]\r\nScreen Width=1920", ["A.esp", "B.esp"]);

        result.Should().Be("[General]\r\nScreen Width=1920\r\n[Game Files]\r\nGameFile0=A.esp\r\nGameFile1=B.esp\r\n");
    }

    [Fact]
    public void RewriteOfNothingBuildsAFileWithOnlyTheSection()
    {
        MorrowindIniGameFiles.Rewrite("", ["A.esp"]).Should().Be("[Game Files]\r\nGameFile0=A.esp\r\n");
    }

    [Fact]
    public void RewriteLeavesOtherLinesOfTheSectionAlone()
    {
        var text = Join("\n", "[Game Files]", "; load these", "GameFile0=Old.esp", "Note=keep", "GameFile1=Older.esp", "[Next]", "x=y");

        var result = MorrowindIniGameFiles.Rewrite(text, ["New.esp"]);

        result.Should().Be(Join("\n", "[Game Files]", "; load these", "GameFile0=New.esp", "Note=keep", "[Next]", "x=y"));
    }

    [Fact]
    public void RewriteWhenTheSectionEndsTheFileWithoutANewlineAddsOne()
    {
        var result = MorrowindIniGameFiles.Rewrite("[Game Files]\nGameFile0=Old.esp", ["A.esp", "B.esp"]);

        result.Should().Be("[Game Files]\nGameFile0=A.esp\nGameFile1=B.esp\n");
    }

    [Fact]
    public void RewriteIsIdempotent()
    {
        var once = MorrowindIniGameFiles.Rewrite(Join("\r\n", Ini), ["A.esp", "B.esp"]);

        MorrowindIniGameFiles.Rewrite(once, ["A.esp", "B.esp"]).Should().Be(once);
    }

    [Fact]
    public void ParseReadsWhatRewriteWrote()
    {
        var text = MorrowindIniGameFiles.Rewrite(Join("\n", Ini), ["Z.esp", "A.esp"]);

        MorrowindIniGameFiles.Parse(text).Should().Equal("Z.esp", "A.esp");
    }
}

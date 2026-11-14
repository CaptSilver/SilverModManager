using FluentAssertions;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;

namespace SilverModManager.Games.Bethesda.LoadOrder.Tests.Formats;

public class OpenMwContentWriterTests
{
    private static HashSet<string> Names(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Owned = Names("Morrowind.esm", "Tribunal.esm", "Bloodmoon.esm", "Old.esp", "Mod.esp", "Tweak.omwaddon");
    private static readonly string[] Ordered = ["Morrowind.esm", "Tribunal.esm", "Bloodmoon.esm", "Mod.esp", "Tweak.omwaddon"];

    private static string[] Fixture() =>
    [
        "# user settings",
        "data=\"/games/Morrowind/Data Files\"",
        "data=\"/mods/other\"",
        "fallback=Water_Height,0",
        "content=builtin.omwscripts",
        "content=Morrowind.esm",
        "content=Tribunal.esm",
        "content=Bloodmoon.esm",
        "content=Old.esp",
        "content=Other.omwaddon",
        "",
        "encoding=win1252",
    ];

    private static string Join(string eol, params string[] lines) => string.Concat(lines.Select(l => l + eol));

    [Fact]
    public void TheOwnedBlockIsRenderedInGivenOrderAfterTheLastUnownedLine()
    {
        var result = OpenMwContentWriter.Rewrite(Join("\n", Fixture()), Ordered, Owned);

        result.Should().Be(Join("\n",
            "# user settings",
            "data=\"/games/Morrowind/Data Files\"",
            "data=\"/mods/other\"",
            "fallback=Water_Height,0",
            "content=builtin.omwscripts",
            "content=Other.omwaddon",
            "content=Morrowind.esm",
            "content=Tribunal.esm",
            "content=Bloodmoon.esm",
            "content=Mod.esp",
            "content=Tweak.omwaddon",
            "",
            "encoding=win1252"));
    }

    [Fact]
    public void ReorderingTheGivenListReordersTheLines()
    {
        var result = OpenMwContentWriter.Rewrite("content=A.esp\ncontent=B.esp\ncontent=C.esp\n", ["C.esp", "A.esp", "B.esp"], Names("A.esp", "B.esp", "C.esp"));

        result.Should().Be("content=C.esp\ncontent=A.esp\ncontent=B.esp\n");
    }

    [Fact]
    public void AnOwnedNameThatIsNotInTheListGetsNoLine()
    {
        var result = OpenMwContentWriter.Rewrite("content=A.esp\ncontent=B.esp\n", ["B.esp"], Names("A.esp", "B.esp"));

        result.Should().Be("content=B.esp\n");
    }

    [Fact]
    public void LinesForNamesNobodyOwnsAreNeverTouched()
    {
        var result = OpenMwContentWriter.Rewrite("content=builtin.omwscripts\ncontent=FromDataFolder.omwaddon\ncontent=A.esp\n", ["A.esp"], Names("A.esp"));

        result.Should().Be("content=builtin.omwscripts\ncontent=FromDataFolder.omwaddon\ncontent=A.esp\n");
    }

    [Fact]
    public void CrLfFilesStayCrLf()
    {
        var result = OpenMwContentWriter.Rewrite(Join("\r\n", Fixture()), Ordered, Owned);

        result.Should().Contain("content=Mod.esp\r\ncontent=Tweak.omwaddon\r\n");
        result.Replace("\r\n", "").Should().NotContain("\n");
        result.Should().StartWith("# user settings\r\ndata=");
    }

    [Fact]
    public void AFileWithoutContentLinesGetsTheBlockAppended()
    {
        var result = OpenMwContentWriter.Rewrite("data=\"/mods\"\nfallback=a,b\n", Ordered, Owned);

        result.Should().Be("data=\"/mods\"\nfallback=a,b\n" +
                           "content=Morrowind.esm\ncontent=Tribunal.esm\ncontent=Bloodmoon.esm\ncontent=Mod.esp\ncontent=Tweak.omwaddon\n");
    }

    [Fact]
    public void ANewlineIsAddedBeforeTheBlockWhenTheFileDoesNotEndInOne()
    {
        var result = OpenMwContentWriter.Rewrite("data=\"/mods\"", ["Mod.esp"], Names("Mod.esp"));

        result.Should().Be("data=\"/mods\"\ncontent=Mod.esp\n");
    }

    [Fact]
    public void ALastUnownedLineWithoutANewlineGetsOneBeforeTheBlock()
    {
        var result = OpenMwContentWriter.Rewrite("content=builtin.omwscripts", ["Mod.esp"], Names("Mod.esp"));

        result.Should().Be("content=builtin.omwscripts\ncontent=Mod.esp\n");
    }

    [Fact]
    public void AnEmptyFileGetsOnlyTheBlock()
    {
        var result = OpenMwContentWriter.Rewrite("", ["z.omwscripts", "b.esp", "game.omwgame"], Names("z.omwscripts", "b.esp", "game.omwgame"));

        result.Should().Be("content=z.omwscripts\ncontent=b.esp\ncontent=game.omwgame\n");
    }

    [Fact]
    public void NamesAreMatchedWithoutRegardToCase()
    {
        var result = OpenMwContentWriter.Rewrite("content=morrowind.ESM\ncontent=old.ESP\n", ["Mod.esp", "Morrowind.esm"], Owned);

        result.Should().Be("content=Mod.esp\ncontent=Morrowind.esm\n");
    }

    [Fact]
    public void ANameDifferingOnlyInCaseIsOwnedAndWrittenOnce()
    {
        var result = OpenMwContentWriter.Rewrite("content=Morrowind.esm\ncontent=mod.ESP\n", ["Morrowind.esm", "Mod.esp"], Names("Morrowind.esm", "Mod.esp"));

        result.Should().Be("content=Morrowind.esm\ncontent=Mod.esp\n");
    }

    [Fact]
    public void ANameInTheListCountsAsOwnedEvenWhenTheOwnedSetOmitsIt()
    {
        var result = OpenMwContentWriter.Rewrite("content=Mod.esp\n", ["Mod.esp"], Names());

        result.Should().Be("content=Mod.esp\n");
    }

    [Fact]
    public void RewritingTwiceChangesNothing()
    {
        var once = OpenMwContentWriter.Rewrite(Join("\n", Fixture()), Ordered, Owned);

        OpenMwContentWriter.Rewrite(once, Ordered, Owned).Should().Be(once);
    }

    [Fact]
    public void ContentNamesComeBackInLineOrder()
    {
        var names = OpenMwContentWriter.ContentNames(Join("\n", Fixture()));

        names.Should().Equal("builtin.omwscripts", "Morrowind.esm", "Tribunal.esm", "Bloodmoon.esm", "Old.esp", "Other.omwaddon");
    }

    [Fact]
    public void ContentNamesDropsADuplicateKeepingTheFirst() =>
        OpenMwContentWriter.ContentNames("content=A.esp\ncontent=B.esp\ncontent=a.ESP\n").Should().Equal("A.esp", "B.esp");

    [Fact]
    public void AGroundcoverNameIsWrittenUnderGroundcoverAndNotContent()
    {
        var result = OpenMwContentWriter.Rewrite("content=Morrowind.esm\ncontent=Grass.esp\n", ["Morrowind.esm", "Mod.esp"], ["Grass.esp"], Names("Morrowind.esm", "Mod.esp", "Grass.esp"));

        result.Should().Be("content=Morrowind.esm\ncontent=Mod.esp\ngroundcover=Grass.esp\n");
    }

    [Fact]
    public void TurningGroundcoverOffMovesTheNameBack()
    {
        var result = OpenMwContentWriter.Rewrite("content=Morrowind.esm\ngroundcover=Grass.esp\n", ["Morrowind.esm", "Grass.esp"], [], Names("Morrowind.esm", "Grass.esp"));

        result.Should().Be("content=Morrowind.esm\ncontent=Grass.esp\n");
    }

    [Fact]
    public void UnownedGroundcoverLinesAndDoubleHashLinesAreUntouched()
    {
        var text = "groundcover=Theirs.esp\n##content=Parked.esp\n##groundcover=Parked2.esp\ncontent=Morrowind.esm\n";

        var result = OpenMwContentWriter.Rewrite(text, ["Morrowind.esm"], ["Grass.esp"], Names("Morrowind.esm", "Grass.esp"));

        // The owned block follows the last unowned groundcover line, so the user's line keeps its place first.
        result.Should().Be("groundcover=Theirs.esp\ngroundcover=Grass.esp\n##content=Parked.esp\n##groundcover=Parked2.esp\ncontent=Morrowind.esm\n");
    }

    [Fact]
    public void ALauncherStyleRewriteReadsBackBothKeys()
    {
        var launcher = "data=\"/games/Morrowind/Data Files\"\r\ncontent=Morrowind.esm\r\ncontent=Mod.esp\r\ngroundcover=Grass.esp\r\n";

        OpenMwContentWriter.ContentNames(launcher).Should().Equal("Morrowind.esm", "Mod.esp", "Grass.esp");
        OpenMwContentWriter.GroundcoverNames(launcher).Should().Equal("Grass.esp");

        var rewritten = OpenMwContentWriter.Rewrite(launcher, ["Morrowind.esm", "Mod.esp"], ["Grass.esp"], Names("Morrowind.esm", "Mod.esp", "Grass.esp"));

        rewritten.Should().Be(launcher);
    }

    [Fact]
    public void ADeployedArchiveGetsAFallbackArchiveLineAfterTheUsersOwn()
    {
        var text = "fallback-archive=Morrowind.bsa\nfallback-archive=Theirs.bsa\ncontent=Morrowind.esm\n";

        var result = OpenMwContentWriter.Rewrite(text, ["Morrowind.esm"], [], ["Meshes.bsa", "Textures.bsa"], Names("Morrowind.esm", "Meshes.bsa", "Textures.bsa"));

        result.Should().Be("fallback-archive=Morrowind.bsa\nfallback-archive=Theirs.bsa\nfallback-archive=Meshes.bsa\nfallback-archive=Textures.bsa\ncontent=Morrowind.esm\n");
    }

    [Fact]
    public void AnArchiveThatIsNoLongerDeployedLosesItsLineAndNoOtherLineMoves()
    {
        var text = "fallback-archive=Theirs.bsa\nfallback-archive=meshes.BSA\ncontent=Morrowind.esm\n";

        var result = OpenMwContentWriter.Rewrite(text, ["Morrowind.esm"], [], [], Names("Morrowind.esm", "Meshes.bsa"));

        result.Should().Be("fallback-archive=Theirs.bsa\ncontent=Morrowind.esm\n");
    }

    [Fact]
    public void AnArchiveLineDifferingOnlyInCaseIsKeptOnceAndNeverDoubled()
    {
        var text = "fallback-archive=meshes.BSA\n";

        var result = OpenMwContentWriter.Rewrite(text, [], [], ["Meshes.bsa"], Names("Meshes.bsa"));

        result.Should().Be("fallback-archive=Meshes.bsa\n");
    }

    [Fact]
    public void ArchiveLinesAreWrittenWithTheFilesOwnLineEndings()
    {
        var result = OpenMwContentWriter.Rewrite("content=Morrowind.esm\r\n", ["Morrowind.esm"], [], ["Meshes.bsa"], Names("Morrowind.esm", "Meshes.bsa"));

        result.Should().Be("content=Morrowind.esm\r\nfallback-archive=Meshes.bsa\r\n");
    }
}

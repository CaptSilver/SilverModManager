using FluentAssertions;
using NexusMods.Games.CreationEngine.Morrowind.OpenMw;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.Morrowind.OpenMw;

public class OpenMwContentWriterTests
{
    private static HashSet<string> Names(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Owned = Names("Morrowind.esm", "Tribunan.esm", "Bloodmoon.esm", "Old.esp", "Mod.esp", "Tweak.omwaddon");
    private static readonly HashSet<string> Deployed = Names("Morrowind.esm", "Tribunan.esm", "Bloodmoon.esm", "Mod.esp", "Tweak.omwaddon");

    private static string[] Fixture() =>
    [
        "# user settings",
        "data=\"/games/Morrowind/Data Files\"",
        "data=\"/mods/other\"",
        "fallback=Water_Height,0",
        "content=builtin.omwscripts",
        "content=Morrowind.esm",
        "content=Tribunan.esm",
        "content=Bloodmoon.esm",
        "content=Old.esp",
        "content=Other.omwaddon",
        "",
        "encoding=win1252",
    ];

    private static string Join(string eol, params string[] lines) => string.Concat(lines.Select(l => l + eol));

    [Fact]
    public void OwnedEntriesAreKeptDroppedAndAppendedWhileEverythingElseStaysPut()
    {
        var existing = Join("\n", Fixture());

        var result = OpenMwContentWriter.Rewrite(existing, Deployed, Owned);

        result.Should().Be(Join("\n",
            "# user settings",
            "data=\"/games/Morrowind/Data Files\"",
            "data=\"/mods/other\"",
            "fallback=Water_Height,0",
            "content=builtin.omwscripts",
            "content=Morrowind.esm",
            "content=Tribunan.esm",
            "content=Bloodmoon.esm",
            "content=Other.omwaddon",
            "content=Mod.esp",
            "content=Tweak.omwaddon",
            "",
            "encoding=win1252"));
    }

    [Fact]
    public void CrLfFilesStayCrLf()
    {
        var existing = Join("\r\n", Fixture());

        var result = OpenMwContentWriter.Rewrite(existing, Deployed, Owned);

        result.Should().Contain("content=Mod.esp\r\ncontent=Tweak.omwaddon\r\n");
        result.Replace("\r\n", "").Should().NotContain("\n");
        result.Should().StartWith("# user settings\r\ndata=");
    }

    [Fact]
    public void AFileWithoutContentLinesGetsTheBlockAppended()
    {
        var result = OpenMwContentWriter.Rewrite("data=\"/mods\"\nfallback=a,b\n", Deployed, Owned);

        result.Should().Be("data=\"/mods\"\nfallback=a,b\n" +
                           "content=Bloodmoon.esm\ncontent=Morrowind.esm\ncontent=Tribunan.esm\ncontent=Mod.esp\ncontent=Tweak.omwaddon\n");
    }

    [Fact]
    public void ANewlineIsAddedBeforeTheBlockWhenTheFileDoesNotEndInOne()
    {
        var result = OpenMwContentWriter.Rewrite("data=\"/mods\"", Names("Mod.esp"), Names("Mod.esp"));

        result.Should().Be("data=\"/mods\"\ncontent=Mod.esp\n");
    }

    [Fact]
    public void ALastContentLineWithoutANewlineGetsOneBeforeTheAppendedBlock()
    {
        var result = OpenMwContentWriter.Rewrite("content=Morrowind.esm", Names("Morrowind.esm", "Mod.esp"), Names("Morrowind.esm", "Mod.esp"));

        result.Should().Be("content=Morrowind.esm\ncontent=Mod.esp\n");
    }

    [Fact]
    public void AnEmptyFileGetsOnlyTheBlockInGroupOrder()
    {
        var names = Names("z.omwscripts", "b.esp", "a.omwaddon", "Morrowind.esm", "game.omwgame", "A.ESP");

        var result = OpenMwContentWriter.Rewrite("", names, names);

        result.Should().Be("content=game.omwgame\ncontent=Morrowind.esm\ncontent=A.ESP\ncontent=a.omwaddon\ncontent=b.esp\ncontent=z.omwscripts\n");
    }

    [Fact]
    public void GameFilesThatAreOwnedAndDeployedStayWhereTheyWere()
    {
        var existing = "content=Tribunan.esm\ncontent=Morrowind.esm\n";

        var result = OpenMwContentWriter.Rewrite(existing, Deployed, Owned);

        result.Should().StartWith("content=Tribunan.esm\ncontent=Morrowind.esm\n");
    }

    [Fact]
    public void ContentNamesAreMatchedWithoutRegardToCase()
    {
        var result = OpenMwContentWriter.Rewrite("content=morrowind.ESM\ncontent=old.ESP\n", Deployed, Owned);

        result.Should().Be("content=morrowind.ESM\ncontent=Bloodmoon.esm\ncontent=Tribunan.esm\ncontent=Mod.esp\ncontent=Tweak.omwaddon\n");
    }
}

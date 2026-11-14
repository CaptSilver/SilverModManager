using System.IO.Compression;
using FluentAssertions;
using SilverModManager.Wabbajack.Mapping;

namespace SilverModManager.Wabbajack.Tests.Mapping;

public class Mo2IniTests
{
    /// <summary>The ModOrganizer.ini the fixture list carries, as the compiler stored it.</summary>
    private static string FixtureIni()
    {
        using var zip = ZipFile.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MO2AndSKSETest.wabbajack"));
        using var modlist = System.Text.Json.JsonDocument.Parse(zip.GetEntry("modlist")!.Open());
        var blob = modlist.RootElement.GetProperty("Directives").EnumerateArray()
            .First(d => d.GetProperty("To").GetString() == "ModOrganizer.ini")
            .GetProperty("SourceDataID").GetString()!;
        using var reader = new StreamReader(zip.GetEntry(blob)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheSelectedProfile_IsReadOutOfItsByteArray()
    {
        Mo2Ini.SelectedProfile(Mo2Ini.Parse(FixtureIni())).Should().Be("MO2AndSKSETest");
    }

    [Fact]
    public void TheFixturesThreeExecutables_ReadBackWithTheirQtEscapesRemoved()
    {
        var executables = Mo2Ini.CustomExecutables(Mo2Ini.Parse(FixtureIni()));

        executables.Select(e => e.Title).Should().Equal("Skyrim Special Edition", "Skyrim Special Edition Launcher", "Explore Virtual Folder");
        executables[0].Binary.Should().Be("{--||GAME_PATH_MAGIC_FORWARD||--}/SkyrimSE.exe");
        executables[0].Arguments.Should().BeEmpty();
        executables[2].Arguments.Should().Be("\"{--||GAME_PATH_MAGIC_DOUBLE_BACK||--}\\data\"");
        executables[2].Binary.Should().Be("{--||MO2_PATH_MAGIC_FORWARD||--}/explorer++/Explorer++.exe");
    }

    [Fact]
    public void CustomExecutablesCarryTheWorkingDirectory()
    {
        // Spelled as in Yggdrasil's ini, which sets some entries' start folder and leaves the others empty.
        var document = Mo2Ini.Parse(
            "[customExecutables]\n1\\binary={--||MO2_PATH_MAGIC_FORWARD||--}/Game Root/a.exe\n1\\title=A\n1\\workingDirectory={--||MO2_PATH_MAGIC_FORWARD||--}/Game Root\n" +
            "2\\binary=b.exe\n2\\title=B\n2\\workingDirectory=\n3\\binary=c.exe\n3\\title=C\n");

        var executables = Mo2Ini.CustomExecutables(document);

        executables[0].WorkingDirectory.Should().Be("{--||MO2_PATH_MAGIC_FORWARD||--}/Game Root");
        executables[1].WorkingDirectory.Should().BeNull("an empty value means MO2 starts the program in its own folder");
        executables[2].WorkingDirectory.Should().BeNull("an entry without the key has none either");
    }

    [Fact]
    public void AnIniWithoutAProfileOrTools_GivesNothing()
    {
        var document = Mo2Ini.Parse("[General]\ngameName=Skyrim\n");

        Mo2Ini.SelectedProfile(document).Should().BeNull();
        Mo2Ini.CustomExecutables(document).Should().BeEmpty();
    }

    [Fact]
    public void ExecutablesAreOrderedByNumber_NotByText()
    {
        var document = Mo2Ini.Parse("[customExecutables]\n10\\binary=ten\n2\\binary=two\n2\\title=Two\n10\\title=Ten\n10\\workingDirectory=x\n");

        Mo2Ini.CustomExecutables(document).Select(e => e.Title).Should().Equal("Two", "Ten");
    }

    [Fact]
    public void AValueQtWrappedInQuotes_LosesTheQuotesAndKeepsItsCommas()
    {
        var document = Mo2Ini.Parse("[General]\nlist=\"a, b\"\nplain=x\n");

        document.Get("General", "list").Should().Be("a, b");
        document.Get("General", "plain").Should().Be("x");
    }

    [Fact]
    public void AnEscapedQuoteInsideAWrappedValue_StaysAndAnEscapedClosingQuoteIsNotAWrapper()
    {
        var document = Mo2Ini.Parse("[General]\nwrapped=\"say \\\"hi\\\"\"\nunwrapped=\"open\\\"\n");

        document.Get("General", "wrapped").Should().Be("say \"hi\"");
        document.Get("General", "unwrapped").Should().Be("\"open\"");
    }
}

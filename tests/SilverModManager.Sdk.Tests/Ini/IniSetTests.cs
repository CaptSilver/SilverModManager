using System.Text;
using FluentAssertions;
using SilverModManager.Sdk.Ini;

namespace SilverModManager.Sdk.Tests.Ini;

public class IniSetTests
{
    private static IniDocument Doc(string text) => IniDocument.Parse(Encoding.UTF8.GetBytes(text));

    private static readonly IniSet Skyrim = new("Skyrim.ini", "SkyrimCustom.ini", "SkyrimPrefs.ini");

    [Fact]
    public void TheLastFileThatHasTheKeyWins()
    {
        var files = new Dictionary<string, IniDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["Skyrim.ini"] = Doc("[General]\nsLanguage=ENGLISH\nsStartingCell=a\n"),
            ["SkyrimCustom.ini"] = Doc("[General]\nsStartingCell=b\n"),
            ["SkyrimPrefs.ini"] = Doc("[General]\nsStartingCell=c\n"),
        };

        Skyrim.Resolve("General", "sStartingCell", name => files.GetValueOrDefault(name)).Should().Be("c");
        Skyrim.Resolve("General", "sLanguage", name => files.GetValueOrDefault(name)).Should().Be("ENGLISH");
    }

    [Fact]
    public void ALaterFileOverridesWithAnEmptyValue()
    {
        var files = new Dictionary<string, IniDocument>
        {
            ["Skyrim.ini"] = Doc("[General]\nsKey=a\n"),
            ["SkyrimCustom.ini"] = Doc("[General]\nsKey=\n"),
        };

        Skyrim.Resolve("General", "sKey", name => files.GetValueOrDefault(name)).Should().BeEmpty();
    }

    [Fact]
    public void AMissingFileIsSkippedAndAKeyInNoFileIsNull()
    {
        var files = new Dictionary<string, IniDocument> { ["SkyrimPrefs.ini"] = Doc("[Display]\nx=1\n") };

        Skyrim.Resolve("Display", "x", name => files.GetValueOrDefault(name)).Should().Be("1");
        Skyrim.Resolve("Display", "y", name => files.GetValueOrDefault(name)).Should().BeNull();
    }

    [Fact]
    public void FileNamesKeepTheirOrder() =>
        Skyrim.FileNames.Should().Equal("Skyrim.ini", "SkyrimCustom.ini", "SkyrimPrefs.ini");
}

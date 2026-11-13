using System.Text;
using FluentAssertions;
using SilverModManager.Games.Larian.Engine.GameInstalled;
using SilverModManager.Games.Larian.Engine.Modsettings;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3.Modsettings;

public class Bg3ModsettingsSchemaTests
{
    private readonly IModsettingsSchema _schema = new Bg3ModsettingsSchema();

    private static readonly ModsettingsDocument.ModuleEntry Gustav =
        new("GustavX", "GustavX", "0", "cb555efe-2d9e-131f-8195-a89329d218ea", "145241302737902957", "");

    private static readonly ModsettingsDocument.ModuleEntry Downloaded =
        new("Cool_Mod", "Cool Mod", "1234567", "11111111-2222-3333-4444-555555555555", "36310271995674624", "abc123");

    private static readonly ModsettingsDocument.ModuleEntry Local =
        new("Local_Mod", "Local Mod", "0", "66666666-7777-8888-9999-000000000000", "1", "");

    private string Written() => _schema.Render(_schema.DefaultVersion, [Gustav, Downloaded, Local], null);

    [Fact]
    public void ParseThenRenderIsByteStableForAFileTheAppWrote()
    {
        var text = Written();

        var doc = _schema.Parse(text);
        var again = _schema.Render(doc.VersionAttributes, doc.Entries, doc);

        again.Should().Be(text);
    }

    [Fact]
    public void ParseThenRenderKeepsABomAndCrlfEndings()
    {
        var text = "\uFEFF" + Written().Replace("\r\n", "\n").Replace("\n", "\r\n");

        var doc = _schema.Parse(text);
        doc.HasBom.Should().BeTrue();
        doc.LineEnding.Should().Be("\r\n");

        var again = _schema.Render(doc.VersionAttributes, doc.Entries, doc);
        again.Should().Be(text);
        new UTF8Encoding(false).GetBytes(again).Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
    }

    [Fact]
    public void AFileWithoutBomOrCrlfIsRenderedWithoutThem()
    {
        var doc = _schema.Parse(Written().Replace("\r\n", "\n"));
        doc.HasBom.Should().BeFalse();
        doc.LineEnding.Should().Be("\n");

        _schema.Render(doc.VersionAttributes, doc.Entries, doc).Should().NotContain("\r").And.NotStartWith("\uFEFF");
    }

    [Fact]
    public void GameEntriesAreTheUnpublishedOnes()
    {
        var doc = _schema.Parse(Written());

        _schema.GameEntries(doc, new HashSet<string>()).Select(e => e.Folder).Should().Equal("GustavX", "Local_Mod");
    }

    [Fact]
    public void BuildsAFileFromNothingWithTheGameModules()
    {
        _schema.CanCreateFromNothing.Should().BeTrue();
        _schema.EntriesWhenNoFile.Select(e => e.Folder).Should().Equal("GustavX", "HonourX");
        _schema.DefaultVersion.Should().Contain(new KeyValuePair<string, string>("major", "4"));
    }
}

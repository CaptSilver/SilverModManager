using System.Text;
using FluentAssertions;
using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class ModsConfigXmlFormatTests
{
    private const string Current = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ModsConfigData>\n  <version>1.5.4297 rev435</version>\n  <activeMods>\n    <li>brrainz.harmony</li>\n    <li>ludeon.rimworld</li>\n    <li>ludeon.rimworld.royalty</li>\n    <li>oskarpotocki.vanillafactionsexpanded.core</li>\n  </activeMods>\n  <knownExpansions>\n    <li>ludeon.rimworld.royalty</li>\n  </knownExpansions>\n</ModsConfigData>";

    private static readonly ModsConfigXmlFormat Format = new();

    [Fact]
    public void ListedReadsTheActiveModsInOrder()
    {
        Format.Listed(Current).Should().Equal("brrainz.harmony", "ludeon.rimworld", "ludeon.rimworld.royalty", "oskarpotocki.vanillafactionsexpanded.core");
    }

    [Fact]
    public void ListedTrimsAndLowerCasesTheNames()
    {
        Format.Listed("<ModsConfigData><activeMods><li> Brrainz.Harmony </li></activeMods></ModsConfigData>").Should().Equal("brrainz.harmony");
    }

    [Fact]
    public void AMalformedFileListsNothing()
    {
        Format.Listed("<ModsConfigData><activeMods>").Should().BeEmpty();
    }

    [Fact]
    public void RenderKeepsVersionAndKnownExpansionsAndReplacesActiveMods()
    {
        var text = Format.Render(["a.b"], Current);

        text.Should().Contain("<version>1.5.4297 rev435</version>").And.Contain("<knownExpansions>");
        text.Split("<li>a.b</li>").Should().HaveCount(2, "the one mod is listed once");
        text.Should().NotContain("brrainz.harmony");
        text.Should().Contain("<li>ludeon.rimworld.royalty</li>", "the known expansions keep theirs");
    }

    [Fact]
    public void RenderWithoutACurrentFileWritesOnlyActiveMods()
    {
        Format.Render(["a.b"], null).Should().Be("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ModsConfigData>\n  <activeMods>\n    <li>a.b</li>\n  </activeMods>\n</ModsConfigData>");
    }

    [Fact]
    public void AMalformedCurrentFileRendersFresh()
    {
        Format.Render(["a.b"], "<ModsConfigData><activeMods>").Should().Be(Format.Render(["a.b"], null));
    }

    [Fact]
    public void AFileWithAnotherRootRendersFresh()
    {
        Format.Render(["a.b"], "<Other><version>1</version></Other>").Should().Be(Format.Render(["a.b"], null));
    }

    [Fact]
    public void ACrlfFileWithABomKeepsItsVersionText()
    {
        var windows = "﻿" + Current.Replace("\n", "\r\n");

        Format.Listed(windows).Should().HaveCount(4);
        var text = Format.Render(["a.b"], windows);

        text.Should().Contain("<version>1.5.4297 rev435</version>");
        text.Should().Contain("<li>ludeon.rimworld.royalty</li>");
        Format.Listed(text).Should().Equal("a.b");
    }

    [Fact]
    public void RenderWritesNoBomAndNoCarriageReturnsOutsideTheKeptElements()
    {
        var text = Format.Render(["a.b"], "﻿" + Current.Replace("\n", "\r\n"));

        text.Should().NotStartWith("﻿");
        text.Replace("\r\n", "\n").Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ModsConfigData>");
    }

    [Theory]
    [InlineData("Harmony/About/About.xml", "Harmony")]
    [InlineData("Harmony/1.5/Assemblies/a.dll", "Harmony")]
    [InlineData("LoadFolders.xml", null)]
    public void ModOfIsTheFirstFolder(string path, string? expected)
    {
        Format.ModOf((RelativePath)path).Should().Be(expected);
    }

    [Fact]
    public void RenderThenListedRoundTrips()
    {
        Format.Listed(Format.Render(["a.b", "c.d"], Current)).Should().Equal("a.b", "c.d");
    }
}

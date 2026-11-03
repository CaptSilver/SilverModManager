using SilverModManager.Games.Bethesda.Loot.Conditions;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Conditions;

// Ports the extract_version tests in libloot 0.29.6's src/plugin/mod.rs.
public class PluginDescriptionVersionTests
{
    [Theory]
    [InlineData("5", "5")]
    [InlineData("10", "10")]
    [InlineData("10.11.12.13", "10.11.12.13")]
    [InlineData("1.0.0-x.7.z.92+exp.sha.5114f85", "1.0.0-x.7.z.92")]
    [InlineData("01.0.0_alpha:1-2 3", "01.0.0_alpha:1-2")]
    [InlineData("v5.0", "5.0")]
    [InlineData("Updated: 10/09/2016 13:15:18\r\n\r\nRecords Changed: 43", "10/09/2016 13:15:18")]
    [InlineData("Version 0.2.", "0.2")]
    [InlineData("Legendary Edition\r\n\r\nVersion: 3.0.0", "3.0.0")]
    [InlineData("fixing over 2,300 bugs so far! Version: 3.5.3", "3.5.3")]
    [InlineData("Version: 2.1 The Unofficial Fallout 3 Patch", "2.1")]
    [InlineData("V2.11\r\n\r\n{{BASH:Invent}}", "2.11")]
    [InlineData("Version:. 1.09", "1.09")]
    [InlineData("comprehensive bugfixing mod for The Elder Scrolls V: Skyrim\r\n\r\nVersion: 2.1.3b\r\n\r\n", "2.1.3b")]
    [InlineData("SkyUI 5.1", "5.1")]
    [InlineData("Requires Skyrim patch 1.9.32.0.8 or greater.\nRequires Unofficial Skyrim Legendary Edition Patch 3.0.0 or greater.\nVersion 2.0.0", "2.0.0")]
    [InlineData("Immersive Armors v8 Main Plugin", "8")]
    [InlineData("Compatibility patch for AOS v2.5 and True Storms v1.5 (or later),\nPatch Version: 1.0", "1.0")]
    [InlineData("Version: 2 {{BASH:C.Water}}", "2")]
    public void Extract_FindsTheVersion(string description, string expected)
    {
        PluginDescriptionVersion.Extract(description).Should().Be(expected);
    }

    [Theory]
    [InlineData("The quick brown fox jumped over the lazy dog.")]
    [InlineData("Adds 8 variants of Triss Merigold's outfit from \"The Witcher 2\"")]
    [InlineData("")]
    [InlineData(null)]
    public void Extract_ReturnsNullWithoutAVersion(string? description)
    {
        PluginDescriptionVersion.Extract(description).Should().BeNull();
    }
}

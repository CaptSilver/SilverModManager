using FluentAssertions;
using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class ModOrderTxtFormatTests
{
    private static readonly ModOrderTxtFormat Format = new();

    [Fact]
    public void TheModsAreOnePerLineInTheOrderGiven()
    {
        Format.Render(["Alpha", "Beta", "Gamma"]).Should().Be("Alpha\nBeta\nGamma");
    }

    [Fact]
    public void NoModsIsAnEmptyFile()
    {
        Format.Render([]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Cool/mod.manifest", "Cool")]
    [InlineData("Cool/Data/cool.pak", "Cool")]
    [InlineData("Cool/Localization/English_xml.pak", "Cool")]
    [InlineData("Cool/config.cfg", "Cool")]
    [InlineData("Cool/Data/COOL.PAK", "Cool")]
    public void AFolderHoldingAManifestPakOrCfgFileIsAMod(string file, string mod)
    {
        Format.ModOf((RelativePath)file).Should().Be(mod);
    }

    [Theory]
    [InlineData("mod_order.txt")]
    [InlineData("loose.pak")]
    [InlineData("Cool/readme.txt")]
    [InlineData("Cool/preview.png")]
    public void AFileThatMakesNoFolderAModIsNobodys(string file)
    {
        Format.ModOf((RelativePath)file).Should().BeNull();
    }
}

using FluentAssertions;

namespace NexusMods.Games.Bethesda.LoadOrder.Tests;

public class PluginFlagsTests
{
    private static PluginHeader Header(string name, uint flags = 0, params string[] masters) => new(
        name, masters, flags, null, null, null, null, PluginFormat.Tes4);

    // IsMaster

    [Fact]
    public void MasterFlagMakesAMaster() => PluginFlags.IsMaster(Header("A.esp", 1), false, false).Should().BeTrue();

    [Fact]
    public void NoFlagNoMaster() => PluginFlags.IsMaster(Header("A.esp"), false, false).Should().BeFalse();

    [Theory]
    [InlineData("A.esm")]
    [InlineData("A.ESM")]
    [InlineData("A.esl")]
    public void ExtensionMakesAMasterWhenTheGameReadsIt(string name) => PluginFlags.IsMaster(Header(name), true, false).Should().BeTrue();

    [Fact]
    public void EspIsNotAMasterByExtension() => PluginFlags.IsMaster(Header("A.esp"), true, false).Should().BeFalse();

    [Fact]
    public void ExtensionIsIgnoredWhenTheGameDoesNotReadIt() => PluginFlags.IsMaster(Header("A.esm"), false, false).Should().BeFalse();

    [Fact]
    public void GhostSuffixIsStrippedBeforeTheExtensionCheck() => PluginFlags.IsMaster(Header("A.esm.ghost"), true, false).Should().BeTrue();

    [Fact]
    public void GhostedEspIsStillNotAMaster() => PluginFlags.IsMaster(Header("A.esp.GHOST"), true, false).Should().BeFalse();

    [Fact]
    public void IgnoredMasterFlagForcesFalseEvenWithFlagAndExtension()
        => PluginFlags.IsMaster(Header("A.esm", 1), true, masterFlagIgnored: true).Should().BeFalse();

    // IsLight

    [Fact]
    public void LightFlag0x200OnSkyrimSe() => PluginFlags.IsLight(Header("A.esp", 0x200), true, false).Should().BeTrue();

    [Fact]
    public void Flag0x100IsNotLightOutsideStarfield() => PluginFlags.IsLight(Header("A.esp", 0x100), true, false).Should().BeFalse();

    [Fact]
    public void LightFlag0x100OnStarfield() => PluginFlags.IsLight(Header("A.esp", 0x100), true, true).Should().BeTrue();

    [Fact]
    public void Flag0x200IsNotLightOnStarfield() => PluginFlags.IsLight(Header("A.esp", 0x200), true, true).Should().BeFalse();

    [Fact]
    public void EslExtensionIsLight() => PluginFlags.IsLight(Header("A.esl"), true, false).Should().BeTrue();

    [Fact]
    public void EslExtensionIsLightOnStarfield() => PluginFlags.IsLight(Header("A.esl"), true, true).Should().BeTrue();

    [Fact]
    public void GhostedEslIsLight() => PluginFlags.IsLight(Header("A.esl.ghost"), true, false).Should().BeTrue();

    [Fact]
    public void StarfieldEslWithUpdateFlagIsNotForcedLight() => PluginFlags.IsLight(Header("A.esl", 0x200), true, true).Should().BeFalse();

    [Fact]
    public void SkyrimSeEslWithFlag0x200IsLight() => PluginFlags.IsLight(Header("A.esl", 0x200), true, false).Should().BeTrue();

    [Fact]
    public void NothingIsLightWhenTheGameHasNoLightPlugins()
    {
        PluginFlags.IsLight(Header("A.esl", 0x200 | 0x100), false, false).Should().BeFalse();
        PluginFlags.IsLight(Header("A.esl", 0x200 | 0x100), false, true).Should().BeFalse();
    }

    // IsMedium

    [Fact]
    public void MediumFlag0x400OnStarfield() => PluginFlags.IsMedium(Header("A.esp", 0x400), true, true).Should().BeTrue();

    [Fact]
    public void MediumFlagIsIgnoredOutsideStarfield() => PluginFlags.IsMedium(Header("A.esp", 0x400), true, false).Should().BeFalse();

    [Fact]
    public void MediumFlagIsIgnoredOnALightPlugin() => PluginFlags.IsMedium(Header("A.esp", 0x400 | 0x100), true, true).Should().BeFalse();

    [Fact]
    public void MediumFlagIsIgnoredOnAnEslFile() => PluginFlags.IsMedium(Header("A.esl", 0x400), true, true).Should().BeFalse();

    // IsUpdate

    [Fact]
    public void UpdateFlagWithAMaster() => PluginFlags.IsUpdate(Header("A.esp", 0x200, "B.esm"), true).Should().BeTrue();

    [Fact]
    public void UpdateFlagWithoutMastersIsNotUpdate() => PluginFlags.IsUpdate(Header("A.esp", 0x200), true).Should().BeFalse();

    [Fact]
    public void UpdateFlagWithLightFlagIsNotUpdate() => PluginFlags.IsUpdate(Header("A.esp", 0x200 | 0x100, "B.esm"), true).Should().BeFalse();

    [Fact]
    public void UpdateFlagWithMediumFlagIsNotUpdate() => PluginFlags.IsUpdate(Header("A.esp", 0x200 | 0x400, "B.esm"), true).Should().BeFalse();

    [Fact]
    public void UpdateFlagIsIgnoredOutsideStarfield() => PluginFlags.IsUpdate(Header("A.esp", 0x200, "B.esm"), false).Should().BeFalse();

    [Fact]
    public void StarfieldEslWithUpdateFlagAndMasterIsUpdate()
    {
        var header = Header("A.esl", 0x200, "B.esm");

        PluginFlags.IsUpdate(header, true).Should().BeTrue();
        PluginFlags.IsLight(header, true, true).Should().BeFalse();
    }

    // IsBlueprint

    [Fact]
    public void BlueprintFlag0x800OnStarfield() => PluginFlags.IsBlueprint(Header("A.esp", 0x800), true).Should().BeTrue();

    [Fact]
    public void BlueprintFlagIsIgnoredOutsideStarfield() => PluginFlags.IsBlueprint(Header("A.esp", 0x800), false).Should().BeFalse();

    [Fact]
    public void NoBlueprintFlagNoBlueprint() => PluginFlags.IsBlueprint(Header("A.esp"), true).Should().BeFalse();
}

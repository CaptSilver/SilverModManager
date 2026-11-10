using FluentAssertions;
using SilverModManager.Games.CreationEngine.CrashLogs;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.CrashLogs;

public class CrashLogPathsTests
{
    private static GamePath Data(string path) => new(LocationId.Game, path);

    [Fact]
    public void AMeshWithoutAMeshesPrefixGetsOne()
    {
        CrashLogPaths.Classify(@"Plants\GlowingMushroomCluster01.nif")
            .Should().Be(Data("Data/meshes/Plants/GlowingMushroomCluster01.nif"));
    }

    [Fact]
    public void ATexturePathUnderDataIsLeftAlone()
    {
        CrashLogPaths.Classify(@"textures\effects\enb\fxBlack_32x32.dds")
            .Should().Be(Data("Data/textures/effects/enb/fxBlack_32x32.dds"));
    }

    [Fact]
    public void AnUppercaseTexturesPrefixIsNotDoubled()
    {
        CrashLogPaths.Classify(@"TEXTURES\cubemaps\OutputCube.dds")
            .Should().Be(Data("Data/TEXTURES/cubemaps/OutputCube.dds"));
    }

    [Fact]
    public void ATextureWithoutAPrefixGetsOne()
    {
        CrashLogPaths.Classify(@"effects\fx.dds").Should().Be(Data("Data/textures/effects/fx.dds"));
    }

    [Fact]
    public void AGameRelativePathDropsTheDataPrefix()
    {
        CrashLogPaths.Classify("Data/Meshes/questMarker_door.nif")
            .Should().Be(Data("Data/Meshes/questMarker_door.nif"));
    }

    [Fact]
    public void AnUppercaseDataPrefixIsDroppedToo()
    {
        CrashLogPaths.Classify(@"DATA\Meshes\x.nif").Should().Be(Data("Data/Meshes/x.nif"));
        CrashLogPaths.Classify("data/textures/x.dds").Should().Be(Data("Data/textures/x.dds"));
    }

    [Fact]
    public void ANifInAFileLineIsAMesh()
    {
        var path = CrashLogPaths.Classify(@"Plants\A.nif")!.Value;
        CrashLogPaths.IsMesh(path).Should().BeTrue();
        CrashLogPaths.IsPlugin(path).Should().BeFalse();
        CrashLogPaths.IsTexture(path).Should().BeFalse();
        CrashLogPaths.IsDll(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("Yggdrasil - Synthesis Patch.esp")]
    [InlineData("Skyrim.esm")]
    [InlineData("ccBGSSSE001-Fish.esl")]
    public void APluginNameMapsToData(string name)
    {
        var path = CrashLogPaths.Classify(name);
        path.Should().Be(Data("Data/" + name));
        CrashLogPaths.IsPlugin(path!.Value).Should().BeTrue();
    }

    [Fact]
    public void AnSkseDllMapsToThePluginsFolder()
    {
        var path = CrashLogPaths.Classify("OpenAnimationReplacer.dll");
        path.Should().Be(Data("Data/SKSE/Plugins/OpenAnimationReplacer.dll"));
        CrashLogPaths.IsDll(path!.Value).Should().BeTrue();
    }

    [Theory]
    [InlineData("Realm of Lorkhan")]
    [InlineData("0x3D01A5C6")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnythingElseIsNull(string text)
    {
        CrashLogPaths.Classify(text).Should().BeNull();
    }

    [Fact]
    public void PathsCompareWithoutRegardToCase()
    {
        CrashLogPaths.Classify(@"Meshes\x.nif").Should().Be(CrashLogPaths.Classify("Data/meshes/X.NIF"));
    }
}

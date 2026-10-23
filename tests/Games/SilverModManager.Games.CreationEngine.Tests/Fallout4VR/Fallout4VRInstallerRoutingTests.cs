using FluentAssertions;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Fallout4VR;

/// <summary>Where Fallout 4 VR's own installers put the common archive layouts.</summary>
public class Fallout4VRInstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<Fallout4VRInstallerRoutingTests, CreationEngine.Fallout4VR.Fallout4VR>(helper)
{
    protected override Version GameVersion => new("1.2.72");

    [Fact]
    public async Task TheLoaderAndItsDllLandAtTheGameRoot() =>
        (await InstallLayout("f4sevr_loader.exe", "f4sevr_0_6_21.dll"))
            .Should().Equal("Game/f4sevr_0_6_21.dll", "Game/f4sevr_loader.exe");

    [Fact]
    public async Task PreloaderFilesLandAtTheGameRoot() =>
        (await InstallLayout("winhttp.dll", "xSE PluginPreloader.xml", "IpHlpAPI.dll"))
            .Should().Equal("Game/IpHlpAPI.dll", "Game/winhttp.dll", "Game/xSE PluginPreloader.xml");

    [Fact]
    public async Task APluginInDataLandsInData() =>
        (await InstallLayout("Data/x.esp")).Should().Equal("Game/Data/x.esp");

    [Fact]
    public async Task ATextureFolderLandsInData() =>
        (await InstallLayout("textures/x.dds")).Should().Equal("Game/Data/textures/x.dds");

    [Fact]
    public async Task AWrapperAroundAPluginIsSteppedInto() =>
        (await InstallLayout("Wrapper/Mod.esp")).Should().Equal("Game/Data/Mod.esp");
}

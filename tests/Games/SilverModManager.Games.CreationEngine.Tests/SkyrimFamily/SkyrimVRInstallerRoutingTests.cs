using FluentAssertions;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.SkyrimFamily;

/// <summary>Where SkyrimVR's own installers put the common archive layouts.</summary>
public class SkyrimVRInstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<SkyrimVRInstallerRoutingTests, CreationEngine.SkyrimVR.SkyrimVR>(helper)
{
    protected override Version GameVersion => new("1.4.15");

    [Fact]
    public async Task TheLoaderAndItsDllLandAtTheGameRoot() =>
        (await InstallLayout("sksevr_loader.exe", "sksevr_1_4_15.dll"))
            .Should().Equal("Game/sksevr_1_4_15.dll", "Game/sksevr_loader.exe");

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

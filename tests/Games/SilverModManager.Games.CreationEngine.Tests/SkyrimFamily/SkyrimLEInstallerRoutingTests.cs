using FluentAssertions;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.SkyrimFamily;

/// <summary>Where SkyrimLE's own installers put the common archive layouts.</summary>
public class SkyrimLEInstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<SkyrimLEInstallerRoutingTests, CreationEngine.SkyrimLE.SkyrimLE>(helper)
{
    protected override Version GameVersion => new("1.9.32");

    [Fact]
    public async Task TheLoaderAndItsDllLandAtTheGameRoot() =>
        (await InstallLayout("skse_loader.exe", "skse_1_9_32.dll"))
            .Should().Equal("Game/skse_1_9_32.dll", "Game/skse_loader.exe");

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

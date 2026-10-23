using FluentAssertions;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Starfield;

/// <summary>Where Starfield's own installers put the common archive layouts.</summary>
public class StarfieldInstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<StarfieldInstallerRoutingTests, CreationEngine.Starfield.Starfield>(helper)
{
    protected override Version GameVersion => new("1.15.222");

    [Fact]
    public async Task TheLoaderAndItsDllLandAtTheGameRoot() =>
        (await InstallLayout("sfse_loader.exe", "sfse_1_15_222.dll"))
            .Should().Equal("Game/sfse_1_15_222.dll", "Game/sfse_loader.exe");

    [Fact]
    public async Task APluginInDataLandsInData() =>
        (await InstallLayout("Data/x.esm")).Should().Equal("Game/Data/x.esm");

    [Fact]
    public async Task ATextureFolderLandsInData() =>
        (await InstallLayout("textures/x.dds")).Should().Equal("Game/Data/textures/x.dds");

    [Fact]
    public async Task AWrapperAroundAPluginIsSteppedInto() =>
        (await InstallLayout("Wrapper/Mod.esm")).Should().Equal("Game/Data/Mod.esm");

    [Fact]
    public async Task AWrapperAroundAnArchiveIsSteppedInto() =>
        (await InstallLayout("Wrapper/Mod.ba2")).Should().Equal("Game/Data/Mod.ba2");
}

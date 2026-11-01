using FluentAssertions;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Fallout4London;

/// <summary>Where Fallout 4 London's own installers put the common archive layouts.</summary>
public class Fallout4LondonInstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<Fallout4LondonInstallerRoutingTests, CreationEngine.Fallout4London.Fallout4London>(helper)
{
    protected override Version GameVersion => new("1.10.163");

    [Fact]
    public async Task TheLoaderAndItsDllLandAtTheGameRoot() =>
        (await InstallLayout("f4se_loader.exe", "f4se_1_10_163.dll"))
            .Should().Equal("Game/f4se_1_10_163.dll", "Game/f4se_loader.exe");

    [Fact]
    public async Task APluginInDataLandsInData() =>
        (await InstallLayout("Data/x.esp")).Should().Equal("Game/Data/x.esp");

    [Fact]
    public async Task ATextureFolderLandsInData() =>
        (await InstallLayout("textures/x.dds")).Should().Equal("Game/Data/textures/x.dds");

    [Fact]
    public async Task AWrapperAroundAPluginIsSteppedInto() =>
        (await InstallLayout("Wrapper/Mod.esp")).Should().Equal("Game/Data/Mod.esp");

    [Fact]
    public async Task AFolderNamedAfterTheGameIsSteppedInto() =>
        (await InstallLayout("Fallout4London/Data/Mod.esp")).Should().Equal("Game/Data/Mod.esp");
}

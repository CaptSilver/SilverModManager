using FluentAssertions;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.FalloutNewVegas;

/// <summary>Where FalloutNewVegas's own installers put the common archive layouts.</summary>
public class FalloutNewVegasInstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<FalloutNewVegasInstallerRoutingTests, CreationEngine.FalloutNewVegas.FalloutNewVegas>(helper)
{
    protected override Version GameVersion => new("1.4.0.525");

    [Fact]
    public async Task TheLoaderAndItsDllLandAtTheGameRoot() =>
        (await InstallLayout("nvse_loader.exe", "nvse_1_4.dll"))
            .Should().Equal("Game/nvse_1_4.dll", "Game/nvse_loader.exe");

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
    public async Task AWrapperAroundAnArchiveIsSteppedInto() =>
        (await InstallLayout("Wrapper/Mod.bsa")).Should().Equal("Game/Data/Mod.bsa");

    [Fact]
    public async Task AFolderNamedAfterTheGameIsSteppedInto() =>
        (await InstallLayout("Fallout New Vegas/Data/Mod.esp")).Should().Equal("Game/Data/Mod.esp");
}

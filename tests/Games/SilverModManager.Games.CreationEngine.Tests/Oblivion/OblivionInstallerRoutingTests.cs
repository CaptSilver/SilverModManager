using FluentAssertions;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Oblivion;

/// <summary>Where Oblivion's own installers put the common archive layouts.</summary>
public class OblivionInstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<OblivionInstallerRoutingTests, CreationEngine.Oblivion.Oblivion>(helper)
{
    protected override Version GameVersion => new("1.2.0.416");

    [Fact]
    public async Task TheLoaderAndItsDllLandAtTheGameRoot() =>
        (await InstallLayout("obse_loader.exe", "obse_1_2_416.dll"))
            .Should().Equal("Game/obse_1_2_416.dll", "Game/obse_loader.exe");

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
        (await InstallLayout("Oblivion/Data/Mod.esp")).Should().Equal("Game/Data/Mod.esp");
}

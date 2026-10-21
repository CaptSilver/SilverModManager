using FluentAssertions;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.Fallout3;

/// <summary>Where Fallout3's own installers put the common archive layouts.</summary>
public class Fallout3InstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<Fallout3InstallerRoutingTests, CreationEngine.Fallout3.Fallout3>(helper)
{
    protected override Version GameVersion => new("1.7.0.3");

    [Fact]
    public async Task TheLoaderAndItsDllLandAtTheGameRoot() =>
        (await InstallLayout("fose_loader.exe", "fose_1_7.dll"))
            .Should().Equal("Game/fose_1_7.dll", "Game/fose_loader.exe");

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
        (await InstallLayout("Fallout 3/Data/Mod.esp")).Should().Equal("Game/Data/Mod.esp");
}

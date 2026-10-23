using FluentAssertions;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.SkyrimFamily;

/// <summary>Where EnderalSE's own installers put the common archive layouts.</summary>
public class EnderalSEInstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<EnderalSEInstallerRoutingTests, CreationEngine.EnderalSE.EnderalSE>(helper)
{
    protected override Version GameVersion => new("2.0.12");

    [Fact]
    public async Task TheLoaderAndItsDllLandAtTheGameRoot() =>
        (await InstallLayout("skse64_loader.exe", "skse64_2_0_20.dll"))
            .Should().Equal("Game/skse64_2_0_20.dll", "Game/skse64_loader.exe");

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

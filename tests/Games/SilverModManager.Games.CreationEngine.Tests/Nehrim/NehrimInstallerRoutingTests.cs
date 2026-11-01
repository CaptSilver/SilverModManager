using FluentAssertions;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Nehrim;

/// <summary>Where Nehrim's own installers put the common archive layouts.</summary>
public class NehrimInstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<NehrimInstallerRoutingTests, CreationEngine.Nehrim.Nehrim>(helper)
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
    public async Task AFolderNamedAfterTheGameIsSteppedInto() =>
        (await InstallLayout("Nehrim/Data/Mod.esp")).Should().Equal("Game/Data/Mod.esp");
}

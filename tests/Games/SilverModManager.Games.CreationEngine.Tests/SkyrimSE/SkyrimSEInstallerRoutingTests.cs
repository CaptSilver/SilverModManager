using FluentAssertions;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.SkyrimSE;

/// <summary>Where Skyrim SE's own installers put the common archive layouts.</summary>
public class SkyrimSEInstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<SkyrimSEInstallerRoutingTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override Version GameVersion => new("1.6.1170");

    [Fact]
    public async Task ADataContentFolderLandsInData() =>
        (await InstallLayout("meshes/a.nif")).Should().Equal("Game/Data/meshes/a.nif");

    [Fact]
    public async Task AnExplicitDataFolderIsKept() =>
        (await InstallLayout("Data/textures/b.dds")).Should().Equal("Game/Data/textures/b.dds");

    [Fact]
    public async Task AWrapperAroundADataFolderIsStrippedAndTheDataFolderKept() =>
        (await InstallLayout("Wrapper/Data/c.esp")).Should().Equal("Game/Data/c.esp");

    [Fact]
    public async Task AWrapperAroundAPluginIsStripped() =>
        (await InstallLayout("Wrapper/d.esp")).Should().Equal("Game/Data/d.esp");

    [Fact]
    public async Task ScriptExtenderFilesLandAtTheGameRoot() =>
        (await InstallLayout("skse64_loader.exe", "skse64_1_6_1170.dll"))
            .Should().Equal("Game/skse64_1_6_1170.dll", "Game/skse64_loader.exe");

    [Theory]
    [InlineData("Skyrim Special Edition")]
    [InlineData("SSE")]
    public async Task AFolderNamedAfterTheGameIsSteppedInto(string folder) =>
        (await InstallLayout($"{folder}/meshes/a.nif")).Should().Equal("Game/Data/meshes/a.nif");

    [Theory]
    [InlineData("Wrapper/Mod.bsa")]
    [InlineData("Wrapper/Mod.ba2")]
    public async Task ALoneArchiveInAWrapperFolderIsSteppedInto(string entry) =>
        (await InstallLayout(entry)).Should().Equal($"Game/Data/{Path.GetFileName(entry)}");
}

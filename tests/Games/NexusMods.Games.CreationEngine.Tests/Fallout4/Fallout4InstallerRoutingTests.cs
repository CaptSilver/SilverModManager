using FluentAssertions;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.Fallout4;

/// <summary>Where Fallout 4's own installers put the common archive layouts.</summary>
public class Fallout4InstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<Fallout4InstallerRoutingTests, CreationEngine.Fallout4.Fallout4>(helper)
{
    protected override Version GameVersion => new("1.10.163");

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
    public async Task ScriptExtenderAndPreloaderFilesLandAtTheGameRoot() =>
        (await InstallLayout("f4se_loader.exe", "winhttp.dll", "xSE PluginPreloader.xml", "IpHlpAPI.dll"))
            .Should().Equal("Game/IpHlpAPI.dll", "Game/f4se_loader.exe", "Game/winhttp.dll", "Game/xSE PluginPreloader.xml");

    [Theory]
    [InlineData("Fallout 4")]
    [InlineData("FO4")]
    public async Task AFolderNamedAfterTheGameIsSteppedInto(string folder) =>
        (await InstallLayout($"{folder}/meshes/a.nif")).Should().Equal("Game/Data/meshes/a.nif");

    // StopPatternInstaller builds the archive pattern as \.{ext} while Extension.ToString() already carries
    // the dot, so the pattern reads \..bsa$ and never matches an archive. These pin what happens today: the
    // wrapper folder is kept instead of being stripped.
    [Theory]
    [InlineData("Wrapper/Mod.bsa")]
    [InlineData("Wrapper/Mod.ba2")]
    public async Task ALoneArchiveInAWrapperFolderKeepsTheWrapper(string entry) =>
        (await InstallLayout(entry)).Should().Equal($"Game/Data/{entry}");
}

using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.CreationEngine.Installers;
using SilverModManager.Sdk.Games;
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

    [Fact]
    public async Task ARootFolderGoesToTheGameFolderAndTheRestToData() =>
        (await InstallLayout("root/openvr_api.dll", "root/opencomposite.ini", "mod.esp"))
            .Should().Equal("Game/Data/mod.esp", "Game/opencomposite.ini", "Game/openvr_api.dll");

    [Fact]
    public async Task ARootFolderKeepsItsSubfolders() =>
        (await InstallLayout("Root/bin/x.dll", "meshes/a.nif"))
            .Should().Equal("Game/Data/meshes/a.nif", "Game/bin/x.dll");

    [Fact]
    public async Task AnArchiveWithoutARootFolderIsUnchanged() =>
        (await InstallLayout("openvr_api.dll", "mod.esp"))
            .Should().Equal("Game/Data/mod.esp", "Game/Data/openvr_api.dll");

    [Fact]
    public async Task AFileNamedRootIsNotAFolder() =>
        (await InstallLayout("root", "mod.esp"))
            .Should().Equal("Game/Data/mod.esp", "Game/Data/root");

    [Fact]
    public async Task ARootFolderInAWrapperGoesToTheGameFolderAndItsDataToData() =>
        (await InstallLayout("ModName/root/openvr_api.dll", "ModName/Data/mod.esp", "ModName/Data/meshes/a.nif"))
            .Should().Equal("Game/Data/meshes/a.nif", "Game/Data/mod.esp", "Game/openvr_api.dll");

    [Fact]
    public async Task ARootFolderAloneInAWrapperGoesToTheGameFolder() =>
        (await InstallLayout("ModName/root/bin/x.dll")).Should().Equal("Game/bin/x.dll");

    [Theory]
    [InlineData("Skyrim Special Edition")]
    [InlineData("SSE")]
    public async Task ARootFolderInAFolderNamedAfterTheGameGoesToTheGameFolder(string folder) =>
        (await InstallLayout($"{folder}/root/openvr_api.dll", $"{folder}/Data/mod.esp"))
            .Should().Equal("Game/Data/mod.esp", "Game/openvr_api.dll");

    [Fact]
    public async Task ARootFolderBesideDataContentInAGameFolderGoesToTheGameFolder() =>
        (await InstallLayout("Skyrim Special Edition/root/openvr_api.dll", "Skyrim Special Edition/meshes/a.nif"))
            .Should().Equal("Game/Data/meshes/a.nif", "Game/openvr_api.dll");

    [Fact]
    public async Task AFolderNamedRootBelowADataContentFolderIsNotARootFolder() =>
        (await InstallLayout("meshes/root/a.nif")).Should().Equal("Game/Data/meshes/root/a.nif");

    [Fact]
    public async Task AFolderNamedRootInsideTheDataFolderStaysInData() =>
        (await InstallLayout("Data/root/a.txt", "Data/mod.esp")).Should().Equal("Game/Data/mod.esp", "Game/Data/root/a.txt");

    [Fact]
    public async Task ARootFolderGoesToTheGameFolderEvenWhenTheEngineFolderIsElsewhere()
    {
        var installer = new StopPatternInstaller(ServiceProvider)
        {
            GameId = CreationEngine.SkyrimSE.SkyrimSE.GameId,
            EnginePath = new GamePath(LocationId.Game, "Bin/Win64"),
            GameAliases = [],
            TopLevelDirs = ["meshes"],
            StopPatterns = [],
            EngineFiles = [],
        }.Build();

        (await InstallLayoutWith(installer, "root/openvr_api.dll", "meshes/a.nif"))
            .Should().Equal("Game/Data/meshes/a.nif", "Game/openvr_api.dll");
    }
}

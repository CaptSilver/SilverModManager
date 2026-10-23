using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.CreationEngine.Morrowind.OpenMw;
using NexusMods.Games.CreationEngine.Tests.Morrowind.OpenMw;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.Morrowind;

/// <summary>Where Morrowind's installers put the common archive layouts: mods go into Data Files, engine files at the game root.</summary>
public class MorrowindInstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<MorrowindInstallerRoutingTests, CreationEngine.Morrowind.Morrowind>(helper)
{
    protected override Version GameVersion => new("1.6.0.1820");

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddTempOpenMwConfigRoots();

    // Locating Morrowind looks for openmw.cfg, which must not find the developer's own.
    [Fact]
    public void OpenMwConfigRootsAreTempFolders() =>
        ServiceProvider.GetRequiredService<IOpenMwConfigRoots>().Should().BeOfType<TempOpenMwConfigRoots>();

    [Fact]
    public async Task APluginInDataFilesLandsInDataFiles() =>
        (await InstallLayout("Data Files/x.esp")).Should().Equal("Game/Data Files/x.esp");

    [Fact]
    public async Task AMeshFolderLandsInDataFiles() =>
        (await InstallLayout("meshes/b.nif")).Should().Equal("Game/Data Files/meshes/b.nif");

    [Fact]
    public async Task MeshesInsideDataFilesLandInDataFiles() =>
        (await InstallLayout("Data Files/meshes/a.nif")).Should().Equal("Game/Data Files/meshes/a.nif");

    [Theory]
    [InlineData("icons/c.dds")]
    [InlineData("bookart/c.dds")]
    [InlineData("splash/c.tga")]
    public async Task MorrowindDataFoldersLandInDataFiles(string entry) =>
        (await InstallLayout(entry)).Should().Equal($"Game/Data Files/{entry}");

    [Theory]
    [InlineData("Mod.omwaddon")]
    [InlineData("scripts.omwscripts")]
    [InlineData("Mod.omwgame")]
    public async Task OpenMwPluginsLandInDataFiles(string entry) =>
        (await InstallLayout(entry)).Should().Equal($"Game/Data Files/{entry}");

    [Fact]
    public async Task TheGraphicsExtenderAndItsProxyDllLandAtTheGameRoot() =>
        (await InstallLayout("MGEXEgui.exe", "d3d8.dll")).Should().Equal("Game/MGEXEgui.exe", "Game/d3d8.dll");

    [Fact]
    public async Task TheScriptExtenderDllLandsAtTheGameRoot() =>
        (await InstallLayout("MWSE.dll")).Should().Equal("Game/MWSE.dll");

    // MWSE Lua mods live under Data Files/MWSE, not next to MWSE.dll.
    [Fact]
    public async Task AnMwseLuaModLandsInDataFiles() =>
        (await InstallLayout("MWSE/mods/x/main.lua")).Should().Equal("Game/Data Files/MWSE/mods/x/main.lua");

    [Fact]
    public async Task AWrapperAroundAnMwseLuaModIsSteppedInto() =>
        (await InstallLayout("Wrapper/MWSE/mods/x/main.lua")).Should().Equal("Game/Data Files/MWSE/mods/x/main.lua");

    [Fact]
    public async Task AWrapperAroundAPluginIsSteppedInto() =>
        (await InstallLayout("Wrapper/Mod.esp")).Should().Equal("Game/Data Files/Mod.esp");

    [Fact]
    public async Task AWrapperAroundAnArchiveIsSteppedInto() =>
        (await InstallLayout("Wrapper/Mod.bsa")).Should().Equal("Game/Data Files/Mod.bsa");

    [Fact]
    public async Task AFolderNamedAfterTheGameIsSteppedInto() =>
        (await InstallLayout("Morrowind/Data Files/Mod.esp")).Should().Equal("Game/Data Files/Mod.esp");
}

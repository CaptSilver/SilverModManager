using FluentAssertions;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.OblivionRemastered;

/// <summary>
/// Where the three mod targets of Oblivion Remastered (the deep Data folder, ~mods and Binaries/Win64) get
/// their files from common archive layouts. One archive per layout: the wrapper-folder rule only applies when
/// the archive has a single top folder.
/// </summary>
public class OblivionRemasteredInstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<OblivionRemasteredInstallerRoutingTests, CreationEngine.OblivionRemastered.OblivionRemastered>(helper)
{
    private const string Data = "Game/OblivionRemastered/Content/Dev/ObvData/Data";
    private const string Mods = "Game/OblivionRemastered/Content/Paks/~mods";
    private const string Win64 = "Game/OblivionRemastered/Binaries/Win64";

    protected override Version GameVersion => new("1.2.0.0");

    [Fact]
    public async Task APakTripleLandsInTheModsFolder() =>
        (await InstallLayout("Mod_P.pak", "Mod_P.ucas", "Mod_P.utoc"))
            .Should().Equal($"{Mods}/Mod_P.pak", $"{Mods}/Mod_P.ucas", $"{Mods}/Mod_P.utoc");

    [Fact]
    public async Task APluginAndItsTexturesInDataLandInTheDeepDataFolder() =>
        (await InstallLayout("Data/Mod.esp", "Data/textures/t.dds"))
            .Should().Equal($"{Data}/Mod.esp", $"{Data}/textures/t.dds");

    [Fact]
    public async Task APluginAloneLandsInTheDeepDataFolder() =>
        (await InstallLayout("Mod.esp")).Should().Equal($"{Data}/Mod.esp");

    [Fact]
    public async Task APakAndAPluginInOneArchiveAreSplitBetweenModsAndData() =>
        (await InstallLayout("Mod_P.pak", "Data/Mod.esp")).Should().Equal($"{Data}/Mod.esp", $"{Mods}/Mod_P.pak");

    [Fact]
    public async Task APakInAWrapperWithAPluginAreSplitBetweenModsAndData() =>
        (await InstallLayout("Wrapper/Mod_P.pak", "Wrapper/Mod.esp")).Should().Equal($"{Data}/Mod.esp", $"{Mods}/Mod_P.pak");

    [Fact]
    public async Task AnArchiveLaidOutFromTheGameFolderLandsAtTheSamePaths() =>
        (await InstallLayout(
            "OblivionRemastered/Content/Paks/~mods/x.pak",
            "OblivionRemastered/Content/Dev/ObvData/Data/x.esp"))
            .Should().Equal($"{Data}/x.esp", $"{Mods}/x.pak");

    [Fact]
    public async Task TheScriptExtenderLoaderAndItsDllLandInBinaries() =>
        (await InstallLayout("obse64_loader.exe", "obse64_1_0_0.dll"))
            .Should().Equal($"{Win64}/obse64_1_0_0.dll", $"{Win64}/obse64_loader.exe");

    [Fact]
    public async Task ScriptExtenderPluginsLandBesideTheLoader() =>
        (await InstallLayout("OBSE/Plugins/p.dll")).Should().Equal($"{Win64}/OBSE/Plugins/p.dll");

    [Fact]
    public async Task AWrapperAroundAPluginIsSteppedInto() =>
        (await InstallLayout("Wrapper/Mod.esp")).Should().Equal($"{Data}/Mod.esp");

    [Fact]
    public async Task AWrapperAroundAnArchiveIsSteppedInto() =>
        (await InstallLayout("Wrapper/Mod.bsa")).Should().Equal($"{Data}/Mod.bsa");

    [Fact]
    public async Task AFolderNamedAfterTheGameIsSteppedInto() =>
        (await InstallLayout("Oblivion Remastered/Data/Mod.esp")).Should().Equal($"{Data}/Mod.esp");

    [Fact]
    public async Task ABlueprintModLandsInLogicMods() =>
        (await InstallLayout("LogicMods/Blueprint.pak")).Should().Equal("Game/OblivionRemastered/Content/Paks/LogicMods/Blueprint.pak");

    [Fact]
    public async Task AUe4ssModLandsUnderItsModsFolder() =>
        (await InstallLayout("MyMod/Scripts/main.lua", "MyMod/enabled.txt"))
            .Should().Equal($"{Win64}/ue4ss/Mods/MyMod/Scripts/main.lua", $"{Win64}/ue4ss/Mods/MyMod/enabled.txt");

    [Fact]
    public async Task ATextureFolderNamedModsStaysInData() =>
        (await InstallLayout("textures/Mods/x.dds")).Should().Equal($"{Data}/textures/Mods/x.dds");

    [Fact]
    public async Task AFileNothingClaimsFallsBackToTheDeepDataFolder() =>
        (await InstallLayout("readme.txt")).Should().Equal($"{Data}/readme.txt");
}

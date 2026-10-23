using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Modlists;
using SilverModManager.Games.CreationEngine.Modlists;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests.Modlists;

public class Mo2InstanceTargetTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Mo2InstanceTargetTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create);

    private IMo2InstanceTarget Target => Game.Should().BeAssignableTo<IMo2InstanceTarget>().Subject;

    private static string[] Names(IEnumerable<GamePath> paths) => paths.Select(p => p.FileName.ToString()).ToArray();

    [Fact]
    public void ASupportedTitle_HasNoUnsupportedReason()
    {
        Target.UnsupportedReason(GameInstallation).Should().BeNull();
    }

    [Fact]
    public void ModFiles_GoUnderTheDataFolder()
    {
        Target.ModFilePath(GameInstallation, RelativePath.FromUnsanitizedInput("meshes/a.nif"))
            .Should().Be(new GamePath(LocationId.Game, "Data/meshes/a.nif"));
    }

    [Fact]
    public void TheProfileSettingsFiles_AreMo2sOwnForSkyrimSE_InMyGames()
    {
        var files = Target.ProfileSettingsFiles(GameInstallation);

        Names(files).Should().Equal("Skyrim.ini", "SkyrimPrefs.ini", "SkyrimCustom.ini");
        files.Should().OnlyContain(f => f.LocationId == LocationId.Preferences);
    }

    [Fact]
    public void AFolderWithThePrimaryExecutable_IsAGameRootFolder()
    {
        var withExe = new HashSet<RelativePath> { RelativePath.FromUnsanitizedInput("SkyrimSE.exe"), RelativePath.FromUnsanitizedInput("Data") };
        var without = new HashSet<RelativePath> { RelativePath.FromUnsanitizedInput("Data") };

        Target.IsGameRootFolder(GameInstallation, "Stock Game", withExe).Should().BeTrue();
        Target.IsGameRootFolder(GameInstallation, "Tools", without).Should().BeFalse();
    }

    [Fact]
    public void EveryTitleHasItsMo2ProfileFiles()
    {
        // Each row follows the iniFiles() of MO2's game plugin for the title.
        Expect(CreationEngine.SkyrimSE.SkyrimSE.GameId, LocationId.Preferences, "Skyrim.ini", "SkyrimPrefs.ini", "SkyrimCustom.ini");
        Expect(CreationEngine.SkyrimVR.SkyrimVR.GameId, LocationId.Preferences, "SkyrimVR.ini", "SkyrimPrefs.ini");
        Expect(CreationEngine.SkyrimLE.SkyrimLE.GameId, LocationId.Preferences, "Skyrim.ini", "SkyrimPrefs.ini");
        Expect(CreationEngine.Enderal.Enderal.GameId, LocationId.Preferences, "Enderal.ini", "EnderalPrefs.ini");
        Expect(CreationEngine.EnderalSE.EnderalSE.GameId, LocationId.Preferences, "Enderal.ini", "EnderalPrefs.ini");
        Expect(CreationEngine.Fallout4.Fallout4.GameId, LocationId.Preferences, "Fallout4.ini", "Fallout4Prefs.ini", "Fallout4Custom.ini");
        Expect(CreationEngine.Fallout4VR.Fallout4VR.GameId, LocationId.Preferences, "Fallout4.ini", "Fallout4Prefs.ini", "Fallout4Custom.ini");
        Expect(CreationEngine.Starfield.Starfield.GameId, LocationId.Preferences, "StarfieldPrefs.ini", "StarfieldCustom.ini");
        Expect(CreationEngine.Oblivion.Oblivion.GameId, LocationId.Preferences, "Oblivion.ini", "OblivionPrefs.ini");
        Expect(CreationEngine.Fallout3.Fallout3.GameId, LocationId.Preferences, "Fallout.ini", "FalloutPrefs.ini", "FalloutCustom.ini", "GECKCustom.ini", "GECKPrefs.ini");
        Expect(CreationEngine.FalloutNewVegas.FalloutNewVegas.GameId, LocationId.Preferences, "Fallout.ini", "FalloutPrefs.ini", "FalloutCustom.ini", "GECKCustom.ini", "GECKPrefs.ini");
        Expect(CreationEngine.Morrowind.Morrowind.GameId, LocationId.Game, "Morrowind.ini");
        CreationEngineMo2Target.ProfileSettingsFiles(CreationEngine.OblivionRemastered.OblivionRemastered.GameId).Should().BeEmpty();
    }

    private static void Expect(GameId game, LocationId location, params string[] names)
    {
        var files = CreationEngineMo2Target.ProfileSettingsFiles(game);
        Names(files).Should().Equal(names);
        files.Should().OnlyContain(f => f.LocationId == location && f.Path == f.FileName);
    }

    [Fact]
    public void OblivionRemastered_IsRefusedWithAReason()
    {
        CreationEngineMo2Target.UnsupportedReason(CreationEngine.OblivionRemastered.OblivionRemastered.GameId)
            .Should().Be("Oblivion Remastered modlists need per-file Unreal routing (Paks, UE4SS) that an MO2 layout does not express");
        CreationEngineMo2Target.UnsupportedReason(CreationEngine.SkyrimSE.SkyrimSE.GameId).Should().BeNull();
    }
}

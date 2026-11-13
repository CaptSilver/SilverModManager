using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Games.Larian.Engine.Emitters;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Larian.Tests.DivinityOriginalSin2DE;

/// <summary>What Divinity: Original Sin 2 Definitive Edition is, as a record on the shared Larian base.</summary>
public class Dos2RecordTests(ITestOutputHelper helper) : Dos2Fixture(helper)
{
    private AbsolutePath LarianFolder => GameInstallation.LocatorResult.Path.FileSystem.GetKnownPath(KnownPath.MyDocumentsDirectory)
        .Combine("Larian Studios/Divinity Original Sin 2 Definitive Edition");

    [Fact]
    public void IdsAndStoresAreTheOnesTheGameShipsUnder()
    {
        Larian.DivinityOriginalSin2DE.DivinityOriginalSin2DE.GameId.Should().Be(GameId.From("Larian.DivinityOriginalSin2DE"));
        Larian.DivinityOriginalSin2DE.DivinityOriginalSin2DE.DisplayName.Should().Be("Divinity: Original Sin 2 Definitive Edition");
        Larian.DivinityOriginalSin2DE.DivinityOriginalSin2DE.NexusModsGameId.Value.Value.Should().Be(2569u);
        Game.StoreIdentifiers.SteamAppIds.Should().Equal(435150u);
        Game.StoreIdentifiers.GOGProductIds.Should().Equal(1584823040L);
        Game.Should().BeAssignableTo<IDxgiGame>();
    }

    [Fact]
    public void TheProfileCarriesTheGamesFolders()
    {
        var profile = Game.Profile;

        profile.GameData.Should().Be(RelativePath.FromUnsanitizedInput("DefEd/Data"));
        profile.Executables.Select(e => e.ToString()).Should().Equal("DefEd/bin/EoCApp.exe");
        profile.PakVersions.Should().Equal(13, 10);
        profile.Profiles.Should().Be(new LarianProfileLayout.PerProfileFolder(RelativePath.FromUnsanitizedInput("PlayerProfiles")));
        profile.HasAdventureModules.Should().BeTrue();
        profile.ScriptExtender.Should().BeNull();
        profile.VarietyId.Value.Should().Be(new Guid("ECC16B09-DA03-4FF0-8F72-FD4353341E9E"));
        profile.GameModuleUuids.Should().HaveCount(23).And.Contain("2bd9bdbe-22ae-4aa2-9c93-205880fc6564");
        profile.NativeLinuxDepotUnsupported.Should().BeFalse();
    }

    [Fact]
    public void TheAppWritesTheModListSoNoInGameActivationOrNativeBuildReport()
    {
        Game.DiagnosticEmitters.OfType<LarianInGameActivationEmitter>().Should().BeEmpty();
        Game.DiagnosticEmitters.OfType<LarianNativeBuildEmitter>().Should().BeEmpty();
        Game.DiagnosticEmitters.OfType<LarianProfileEmitter>().Should().ContainSingle();
    }

    [Fact]
    public void LocationsResolveDocumentsIntoThePrefix()
    {
        GameInstallation.Locations[LarianConstants.ModsLocationId].Path.Should().Be(LarianFolder.Combine("Mods"));
        GameInstallation.Locations[LarianConstants.PlayerProfilesLocationId].Path.Should().Be(LarianFolder.Combine("PlayerProfiles"));
        GameInstallation.Locations.Should().NotContainKey(LarianConstants.ScriptExtenderConfigLocationId);
    }

    [Fact]
    public void ModsGoToTheModsFolderAndLooseFilesToDefEdData()
    {
        var targets = Game.LibraryItemInstallers.OfType<GenericPatternMatchInstaller>().Single().InstallFolderTargets;

        targets.Select(t => t.DestinationGamePath).Should().BeEquivalentTo(
        [
            new GamePath(LarianConstants.ModsLocationId, ""),
            new GamePath(LocationId.Game, "DefEd/Data"),
            new GamePath(LocationId.Game, "DefEd/bin"),
        ]);
    }

    [Fact]
    public void ThePlayerNamesTheTwoLocationsItCanFind()
    {
        Game.LocationNames[LarianConstants.ModsLocationId.ToString()].Should().Be("Mods");
        Game.LocationNames[LarianConstants.PlayerProfilesLocationId.ToString()].Should().Be("Profiles");
    }
}

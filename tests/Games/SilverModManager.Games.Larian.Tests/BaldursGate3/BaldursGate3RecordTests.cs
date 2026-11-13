using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Games.Larian.BaldursGate3;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Games.Larian.Engine.Emitters;
using SilverModManager.Games.Larian.Engine.Installers;
using SilverModManager.Games.Larian.Engine.Modsettings;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3;

/// <summary>What Baldur's Gate 3 is, now that it is a record on the shared Larian base.</summary>
public class BaldursGate3RecordTests(IServiceProvider serviceProvider)
{
    private Larian.BaldursGate3.BaldursGate3 Game
        => serviceProvider.GetServices<IGame>().OfType<Larian.BaldursGate3.BaldursGate3>().Single();

    [Fact]
    public void TheGameIdAndPersistedIdsAreUnchanged()
    {
        Larian.BaldursGate3.BaldursGate3.GameId.Should().Be(GameId.From("Larian.BaldursGate3"));
        Game.Profile.VarietyId.Value.Should().Be(new Guid("A3D1F6B2-5C84-4E0B-9F27-6B1D0C8E2A55"));
        Bg3Constants.ModsLocationId.ToString().Should().Be("Mods");
        Bg3Constants.PlayerProfilesLocationId.ToString().Should().Be("PlayerProfiles");
        Bg3Constants.ScriptExtenderConfigLocationId.ToString().Should().Be("ScriptExtenderConfig");
    }

    [Fact]
    public void TheEmitterSetIsTheBaseListPlusTheLarianOnes()
    {
        Game.DiagnosticEmitters.Select(e => e.GetType()).Should().BeEquivalentTo(
        [
            typeof(NoWayToSourceFilesOnDisk),
            typeof(UndeployableLoadoutDueToMissingGameFiles),
            typeof(ProtonPrefixEmitter),
            typeof(OldVcRuntimeEmitter),
            typeof(OpenXrRuntimeEmitter),
            typeof(LarianDependencyEmitter),
            typeof(DuplicateModuleEmitter),
            typeof(LarianModListReplacedEmitter),
            typeof(LarianModListResetEmitter),
        ]);
        Game.DiagnosticEmitters.OfType<ProtonPrefixEmitter>().Should().ContainSingle();
    }

    [Fact]
    public void TheProfileCarriesWhatTheClassHardCodedBefore()
    {
        var profile = Game.Profile;

        profile.GameName.Should().Be("Baldur's Gate 3");
        profile.LarianFolder.Should().Be(RelativePath.FromUnsanitizedInput("Larian Studios/Baldur's Gate 3"));
        profile.Profiles.Should().Be(new LarianProfileLayout.Fixed(RelativePath.FromUnsanitizedInput("PlayerProfiles/Public")));
        profile.GameData.Should().Be(RelativePath.FromUnsanitizedInput("Data"));
        profile.PakVersions.Should().Equal(15, 16, 18);
        profile.Schema.Should().BeOfType<Bg3ModsettingsSchema>();
        profile.ScriptExtender!.DllPath.Should().Be(new GamePath(LocationId.Game, "bin/DWrite.dll"));
    }

    [Fact]
    public void TheLoadOrderIsStoredUnderTheProfilesVarietyId()
    {
        Game.SortOrderManager.GetSortOrderVarieties().Select(v => v.SortOrderVarietyId)
            .Should().Equal(Game.Profile.VarietyId);
    }

    [Fact]
    public void InstallerTargetsComeFromTheProfile()
    {
        var installers = Game.LibraryItemInstallers;
        installers.Should().HaveCount(2);
        installers[0].Should().BeOfType<LarianScriptExtenderInstaller>();

        var targets = installers[1].Should().BeOfType<GenericPatternMatchInstaller>().Subject.InstallFolderTargets;
        targets.Select(t => t.DestinationGamePath).Should().Equal(
            new GamePath(LarianConstants.ModsLocationId, ""),
            new GamePath(LocationId.Game, "bin"),
            new GamePath(LocationId.Game, "Data"));

        targets[0].KnownSourceFolderNames.Should().Equal("Mods");
        targets[1].KnownSourceFolderNames.Should().Equal("bin");
        targets[2].KnownSourceFolderNames.Should().Equal("Data");
    }

    [Fact]
    public void TheExecutablesAreTheLauncherThenTheEngineBinary()
    {
        Game.Profile.Executables.Select(e => e.ToString()).Should().Equal("Launcher/LariLauncher.exe", "bin/bg3");
    }
}

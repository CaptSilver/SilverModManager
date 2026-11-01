using DynamicData.Kernel;
using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.Generic.Tests.Simple;

public class SimpleGameTests(IServiceProvider serviceProvider)
{
    private SimpleGame GameOf(SimpleGameDefinition definition) => new(serviceProvider, definition);

    private static SimpleGameDefinition Plain => SimpleDefinitions.With(new ModLocation(ModLocationRoot.GameFolder, "Mods"));

    [Fact]
    public void Identity_ComesFromTheDefinition()
    {
        var definition = Plain with
        {
            GameId = GameId.From("Tests.Identity"),
            DisplayName = "Identity Game",
            NexusModsGameId = Sdk.NexusModsApi.NexusModsGameId.From(4321),
            Stores = new StoreIdentifiers(GameId.From("Tests.Identity")) { SteamAppIds = [99u] },
        };

        IGameData game = GameOf(definition);

        game.GameId.Should().Be(GameId.From("Tests.Identity"));
        game.DisplayName.Should().Be("Identity Game");
        game.NexusModsGameId.Value.Should().Be(Sdk.NexusModsApi.NexusModsGameId.From(4321));
        game.StoreIdentifiers.SteamAppIds.Should().Equal(99u);
    }

    [Fact]
    public void PrimaryFile_IsTheExecutableInTheGameFolder()
    {
        var game = GameOf(Plain with { Executable = "System/game.exe" });

        game.GetPrimaryFile(null!).Should().Be(new GamePath(LocationId.Game, "System/game.exe"));
    }

    [Fact]
    public void NativeLinuxExecutable_SetsTheNativeBuildFlags()
    {
        IGameData native = GameOf(Plain with { NativeLinuxExecutable = (RelativePath)"game.x86_64" });
        IGameData windowsOnly = GameOf(Plain);

        native.HasNativeLinuxBuild.Should().BeTrue();
        native.NativeLinuxPrimaryFile.Should().Be((RelativePath)"game.x86_64");
        windowsOnly.HasNativeLinuxBuild.Should().BeFalse();
    }

    [Fact]
    public void IdentifyingFiles_AreTheDefinitions()
    {
        var game = GameOf(Plain with { IdentifyingFiles = ["Data/Only.esm"] });

        ((IGameIdentifyingFiles)game).IdentifyingFiles.Should().Equal((RelativePath)"Data/Only.esm");
        ((IGameIdentifyingFiles)GameOf(Plain)).IdentifyingFiles.Should().BeEmpty();
    }

    [Fact]
    public void Installers_PutMarkerRulesBeforeTheFolderTargets()
    {
        var game = GameOf(Plain with
        {
            Install = new SimpleInstallRules
            {
                MarkerRules = [new SimpleMarkerRule { MarkerFile = "cook.hash", Destination = "UserContent" }],
                Targets = [new SimpleInstallTarget { Destination = "CookedPC" }],
            },
        });

        // The install job takes the first installer that supports the archive, so the order is behaviour.
        game.LibraryItemInstallers.Select(installer => installer.GetType())
            .Should().Equal(typeof(PredicateBasedInstaller), typeof(GenericPatternMatchInstaller));
    }

    [Fact]
    public void Installers_AreOnlyTheKindsTheRulesDeclare()
    {
        GameOf(Plain with { Install = new SimpleInstallRules { Targets = [new SimpleInstallTarget { Destination = "Mods" }] } })
            .LibraryItemInstallers.Select(installer => installer.GetType()).Should().Equal(typeof(GenericPatternMatchInstaller));

        GameOf(Plain with { Install = new SimpleInstallRules() }).LibraryItemInstallers.Should().BeEmpty();
    }

    [Fact]
    public void Installers_AnAsIsRuleAddsOneInstallerThatTakesAnyLayout()
    {
        var game = GameOf(SimpleDefinitions.With(new ModLocation(ModLocationRoot.GameFolder, "GameData")) with
        {
            Install = new SimpleInstallRules { InstallAsIs = true },
        });

        var installer = game.LibraryItemInstallers.Should().ContainSingle().Which.Should().BeOfType<PredicateBasedInstaller>().Subject;

        installer.Destination.Should().Be(new GamePath(LocationId.Game, "GameData"));
    }

    [Fact]
    public void Installers_AnAsIsRuleComesAfterTheMarkerRulesAndTargets()
    {
        // It accepts everything, so any installer behind it would never be asked.
        var game = GameOf(Plain with
        {
            Install = new SimpleInstallRules
            {
                InstallAsIs = true,
                MarkerRules = [new SimpleMarkerRule { MarkerFile = "cook.hash", Destination = "UserContent" }],
                Targets = [new SimpleInstallTarget { Destination = "CookedPC" }],
            },
        });

        game.LibraryItemInstallers.Select(installer => installer.GetType())
            .Should().Equal(typeof(PredicateBasedInstaller), typeof(GenericPatternMatchInstaller), typeof(PredicateBasedInstaller));
        game.LibraryItemInstallers.Last().Should().BeOfType<PredicateBasedInstaller>()
            .Which.Destination.Should().Be(new GamePath(LocationId.Game, "Mods"));
    }

    [Fact]
    public void Installers_TargetThePathBelowTheModRoot()
    {
        var game = GameOf(SimpleDefinitions.With(new ModLocation(ModLocationRoot.GameFolder, "Data")) with
        {
            Install = new SimpleInstallRules
            {
                Targets = [new SimpleInstallTarget { Destination = "Override", KnownSourceFolderNames = ["Override"], KnownValidFileExtensions = [new Extension(".tlk")] }],
            },
        });

        var installer = game.LibraryItemInstallers.Should().ContainSingle().Which.Should().BeOfType<GenericPatternMatchInstaller>().Subject;

        var target = installer.InstallFolderTargets.Should().ContainSingle().Subject;
        target.DestinationGamePath.Should().Be(new GamePath(LocationId.Game, "Data/Override"));
        target.KnownSourceFolderNames.Should().Equal((RelativePath)"Override");
        target.KnownValidFileExtensions.Should().Equal(new Extension(".tlk"));
    }
}

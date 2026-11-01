using FluentAssertions;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unreal.Tests.Fixtures;

/// <summary>
/// What every row of <see cref="UnrealGames"/> must do against its own definition: be found by its executable and
/// take paks, Blueprint mods and UE4SS mods into its project folder. A game's test class derives from this, so a row
/// cannot be added without these running against it.
/// </summary>
public abstract class AUnrealRowTests<TTest>(ITestOutputHelper outputHelper, UnrealGameDefinition definition)
    : AUnrealGameTests<TTest>(outputHelper, definition)
{
    private string Project => Definition.ProjectFolder.ToString();

    private ALibraryArchiveInstaller Installer => Game.LibraryItemInstallers.OfType<UnrealModInstaller>().Single();

    [Fact]
    public void TheLocatedFolderHoldsTheExecutableTheGameLaunches()
    {
        var primaryFile = Game.GetPrimaryFile(GameInstallation);

        primaryFile.Should().Be(new GamePath(LocationId.Game, Definition.Executable));
        GameInstallation.Locations[LocationId.Game].Path.Combine(primaryFile.Path).FileExists
            .Should().BeTrue($"{Definition.DisplayName} is found by {Definition.Executable}");
    }

    [Fact]
    public void TheGameFolderIsTheOnlyLocation()
    {
        GameInstallation.Locations.Keys.Should().BeEquivalentTo([LocationId.Game]);
    }

    [Fact]
    public async Task APakWithItsCompanionsInstallsBelowTheModsFolderOfTheProject()
    {
        var archive = await AddFromPaths("Mod_P.pak", "Mod_P.utoc", "Mod_P.ucas");

        var (result, targets) = await TryInstall(Installer, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(
            OnGame($"{Project}/Content/Paks/~mods/Mod_P.pak"),
            OnGame($"{Project}/Content/Paks/~mods/Mod_P.utoc"),
            OnGame($"{Project}/Content/Paks/~mods/Mod_P.ucas"));
    }

    [Fact]
    public async Task ABlueprintModInstallsBelowLogicMods()
    {
        var archive = await AddFromPaths("LogicMods/Blueprint.pak");

        var (result, targets) = await TryInstall(Installer, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame($"{Project}/Content/Paks/LogicMods/Blueprint.pak"));
    }

    [Fact]
    public async Task AnUe4ssModInstallsBelowTheUe4ssFolderBesideTheBinary()
    {
        var archive = await AddFromPaths("SomeMod/Scripts/main.lua", "SomeMod/enabled.txt");

        var (result, targets) = await TryInstall(Installer, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(
            OnGame($"{Project}/Binaries/Win64/ue4ss/Mods/SomeMod/Scripts/main.lua"),
            OnGame($"{Project}/Binaries/Win64/ue4ss/Mods/SomeMod/enabled.txt"));
    }

    [Fact]
    public async Task ADocumentBesideThePakIsLeftOut()
    {
        var archive = await AddFromPaths("Mod_P.pak", "README.txt");

        var (result, targets) = await TryInstall(Installer, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame($"{Project}/Content/Paks/~mods/Mod_P.pak"));
    }

    [Fact]
    public async Task AnArchiveWithAFileTheLayoutDoesNotKnowIsNotSupported()
    {
        // A BepInEx plugin: the game has nowhere to put the DLL, and dropping it would install half a mod.
        var archive = await AddFromPaths("BepInEx/plugins/Plugin.dll");

        var (result, _) = await TryInstall(Installer, archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("Plugin.dll");
    }

    [Fact]
    public async Task AnArchiveHoldingOnlyDocumentsIsNotSupported()
    {
        var archive = await AddFromPaths("README.txt");

        var (result, _) = await TryInstall(Installer, archive);

        result.IsNotSupported(out _).Should().BeTrue();
    }
}

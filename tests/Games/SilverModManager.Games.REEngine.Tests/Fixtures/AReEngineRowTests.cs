using FluentAssertions;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.REEngine.Tests.Fixtures;

/// <summary>
/// What every row of <see cref="ReEngineGames"/> must do against its own definition: be found by its executable, take
/// loose game files and REFramework scripts and plugins into the game folder, and take the loader's DLL. A game's test
/// class derives from this, so a row cannot be added without these running against it.
/// </summary>
public abstract class AReEngineRowTests<TTest>(ITestOutputHelper outputHelper, ReEngineGameDefinition definition)
    : AReEngineGameTests<TTest>(outputHelper, definition)
{
    private ALibraryArchiveInstaller ModInstaller => Game.LibraryItemInstallers.OfType<ReEngineModInstaller>().Single();

    private ALibraryArchiveInstaller PackInstaller => Game.LibraryItemInstallers.OfType<ReFrameworkPackInstaller>().Single();

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
    public async Task LooseGameFilesInstallBelowNativesInTheGameFolder()
    {
        var archive = await AddFromPaths("natives/STM/art/model/a.mesh.1", "natives/STM/art/b.tex.2");

        var (result, targets) = await TryInstall(ModInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("natives/STM/art/model/a.mesh.1"), OnGame("natives/STM/art/b.tex.2"));
    }

    [Fact]
    public async Task ScriptsAndPluginsInstallBelowReframework()
    {
        var archive = await AddFromPaths("reframework/autorun/mod.lua", "reframework/plugins/mod.dll", "reframework/data/mod.json");

        var (result, targets) = await TryInstall(ModInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("reframework/autorun/mod.lua"), OnGame("reframework/plugins/mod.dll"), OnGame("reframework/data/mod.json"));
    }

    [Fact]
    public async Task AFolderAboveTheModIsStepIntoAndTheFolderNameIsLowerCased()
    {
        var archive = await AddFromPaths("Some Mod v1/Natives/STM/a.mesh.1", "Some Mod v1/REFramework/autorun/mod.lua");

        var (result, targets) = await TryInstall(ModInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("natives/STM/a.mesh.1"), OnGame("reframework/autorun/mod.lua"));
    }

    [Fact]
    public async Task AReadmeBesideTheModIsLeftOut()
    {
        var archive = await AddFromPaths("natives/STM/a.mesh.1", "README.txt");

        var (result, targets) = await TryInstall(ModInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("natives/STM/a.mesh.1"));
    }

    [Fact]
    public async Task AFileOutsideTheTwoFoldersMakesTheArchiveNotSupported()
    {
        var archive = await AddFromPaths("natives/STM/a.mesh.1", "BepInEx/plugins/Plugin.dll");

        var (result, _) = await TryInstall(ModInstaller, archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("Plugin.dll");
    }

    [Fact]
    public async Task TheLoaderDownloadInstallsOnlyTheProxyDll()
    {
        var archive = await AddArchiveNamed(Definition.ReFrameworkRelease, "dinput8.dll", "openvr_api.dll", "openxr_loader.dll");

        var (result, targets) = await TryInstall(PackInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("dinput8.dll"));
    }

    [Fact]
    public async Task TheLoaderInAFolderInstallsAtTheGameRoot()
    {
        var archive = await AddArchiveNamed(Definition.ReFrameworkRelease, "REFramework/dinput8.dll");

        var (result, targets) = await TryInstall(PackInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("dinput8.dll"));
    }

    [Fact]
    public async Task TheLoaderIsNotTakenForAMod()
    {
        var archive = await AddArchiveNamed(Definition.ReFrameworkRelease, "dinput8.dll");

        var (result, _) = await TryInstall(ModInstaller, archive);

        result.IsNotSupported(out _).Should().BeTrue();
    }

    [Fact]
    public async Task AModIsNotTakenForTheLoader()
    {
        var archive = await AddFromPaths("natives/STM/a.mesh.1");

        var (result, _) = await TryInstall(PackInstaller, archive);

        result.IsNotSupported(out _).Should().BeTrue();
    }

    [Fact]
    public async Task AModThatShipsItsOwnLoaderCopyIsLeftToNeitherInstaller()
    {
        var archive = await AddFromPaths("dinput8.dll", "reframework/autorun/mod.lua");

        var (pack, _) = await TryInstall(PackInstaller, archive);
        var (mod, _) = await TryInstall(ModInstaller, archive);

        pack.IsNotSupported(out _).Should().BeTrue();
        mod.IsNotSupported(out _).Should().BeTrue();
    }
}

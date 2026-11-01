using FluentAssertions;
using SilverModManager.Games.Unity.BepInEx;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.Tests.Fixtures;

/// <summary>
/// What every row of <see cref="UnityGames"/> must do against its own definition: be found by its executable and
/// take a BepInEx plugin and loader pack into the places the loader reads them from. A game's test class derives
/// from this, so a row cannot be added without these running against it.
/// </summary>
public abstract class AUnityRowTests<TTest>(ITestOutputHelper outputHelper, UnityGameDefinition definition)
    : AUnityGameTests<TTest>(outputHelper, definition)
{
    private BepInExPluginInstaller PluginInstaller => Game.LibraryItemInstallers.OfType<BepInExPluginInstaller>().Single();

    private BepInExPackInstaller PackInstaller => Game.LibraryItemInstallers.OfType<BepInExPackInstaller>().Single();

    [Fact]
    public void TheLocatedFolderHoldsTheExecutableTheGameLaunches()
    {
        var primaryFile = Game.GetPrimaryFile(GameInstallation);

        primaryFile.Should().Be(new GamePath(LocationId.Game, Definition.Executable));
        GameInstallation.Locations[LocationId.Game].Path.Combine(primaryFile.Path).FileExists
            .Should().BeTrue($"{Definition.DisplayName} is found by {Definition.Executable}");
    }

    [Fact]
    public async Task APluginArchiveInstallsBelowBepInExPlugins()
    {
        var archive = await AddArchiveNamed("Someone-SomePlugin-1.0.0.zip", "SomePlugin.dll", "config/some.plugin.cfg");

        var (result, targets) = await TryInstall(PluginInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("BepInEx/plugins/Someone-SomePlugin/SomePlugin.dll"), OnGame("BepInEx/config/some.plugin.cfg"));
    }

    [Fact]
    public async Task ALoaderPackNamedAfterTheGamesPackInstallsAtTheGameRoot()
    {
        var archive = await AddArchiveNamed($"{Definition.ThunderstoreLoaderPackage}-5.4.2305.zip",
            "BepInExPack/winhttp.dll", "BepInExPack/doorstop_config.ini", "BepInExPack/BepInEx/core/BepInEx.Preloader.dll");

        var (result, targets) = await TryInstall(PackInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("winhttp.dll"), OnGame("doorstop_config.ini"), OnGame("BepInEx/core/BepInEx.Preloader.dll"));
    }

    [Fact]
    public async Task ALayoutNeitherInstallerRecognisesIsNotSupported()
    {
        // A mod for another loader: a DLL under a folder BepInEx never reads, and not a pack.
        var archive = await AddArchiveNamed("Someone-OtherLoaderMod-1.0.0.zip", "QMods/OtherLoaderMod/mod.json", "QMods/OtherLoaderMod/OtherLoaderMod.dll");

        var (plugin, _) = await TryInstall(PluginInstaller, archive);
        var (pack, _) = await TryInstall(PackInstaller, archive);

        plugin.IsNotSupported(out _).Should().BeTrue();
        pack.IsNotSupported(out _).Should().BeTrue();
    }

    [Fact]
    public async Task APluginArchiveIsNotTakenForTheLoaderPack()
    {
        var archive = await AddArchiveNamed("Someone-SomePlugin-1.0.0.zip", "SomePlugin.dll");

        var (result, _) = await TryInstall(PackInstaller, archive);

        result.IsNotSupported(out _).Should().BeTrue();
    }
}

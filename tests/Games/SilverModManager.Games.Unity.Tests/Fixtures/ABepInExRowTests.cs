using FluentAssertions;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.Unity.BepInEx;

namespace SilverModManager.Games.Unity.Tests.Fixtures;

/// <summary>The shared checks of a row whose loader is BepInEx.</summary>
public abstract class ABepInExRowTests<TTest>(ITestOutputHelper outputHelper, UnityGameDefinition definition)
    : AUnityRowTests<TTest>(outputHelper, definition)
{
    protected override ALibraryArchiveInstaller PackInstaller => Game.LibraryItemInstallers.OfType<BepInExPackInstaller>().Single();

    protected override ALibraryArchiveInstaller ModInstaller => Game.LibraryItemInstallers.OfType<BepInExPluginInstaller>().Single();

    protected override string[] PackPaths => ["BepInExPack/winhttp.dll", "BepInExPack/doorstop_config.ini", "BepInExPack/BepInEx/core/BepInEx.Preloader.dll"];

    protected override string[] ExpectedPackTargets => ["winhttp.dll", "doorstop_config.ini", "BepInEx/core/BepInEx.Preloader.dll"];

    // A DLL under a folder BepInEx never reads, and not a pack.
    protected override string[] ForeignModPaths => ["QMods/OtherLoaderMod/mod.json", "QMods/OtherLoaderMod/OtherLoaderMod.dll"];

    [Fact]
    public async Task APluginArchiveInstallsBelowBepInExPlugins()
    {
        var archive = await AddArchiveNamed("Someone-SomePlugin-1.0.0.zip", "SomePlugin.dll", "config/some.plugin.cfg");

        var (result, targets) = await TryInstall(ModInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("BepInEx/plugins/Someone-SomePlugin/SomePlugin.dll"), OnGame("BepInEx/config/some.plugin.cfg"));
    }
}

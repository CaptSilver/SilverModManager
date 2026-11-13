using FluentAssertions;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.Unity.MelonLoader;

namespace SilverModManager.Games.Unity.Tests.Fixtures;

/// <summary>The shared checks of a row whose loader is MelonLoader.</summary>
public abstract class AMelonLoaderRowTests<TTest>(ITestOutputHelper outputHelper, UnityGameDefinition definition)
    : AUnityRowTests<TTest>(outputHelper, definition)
{
    protected override ALibraryArchiveInstaller PackInstaller => Game.LibraryItemInstallers.OfType<MelonLoaderPackInstaller>().Single();

    protected override ALibraryArchiveInstaller ModInstaller => Game.LibraryItemInstallers.OfType<MelonLoaderModInstaller>().Single();

    protected override string[] PackPaths => ["version.dll", "dobby.dll", "MelonLoader/net6/MelonLoader.dll", "manifest.json", "README.md", "icon.png"];

    protected override string[] ExpectedPackTargets => ["version.dll", "dobby.dll", "MelonLoader/net6/MelonLoader.dll"];

    // Only Thunderstore's own metadata: nothing MelonLoader would load.
    protected override string[] ForeignModPaths => ["manifest.json", "README.md", "icon.png"];

    [Fact]
    public async Task AModDllInstallsBelowMods()
    {
        var archive = await AddArchiveNamed("Someone-SomeMod-1.0.0.zip", "SomeMod.dll");

        var (result, targets) = await TryInstall(ModInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("Mods/SomeMod.dll"));
    }
}

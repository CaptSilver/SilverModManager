using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.Unity.BepInEx;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Games.Unity.Tests.Fixtures;

/// <summary>Runs the loader pack installer on a test archive and reports where its files would land.</summary>
public abstract class AUnityInstallerTests<TTest>(ITestOutputHelper outputHelper) : AUnityLoaderInstallerTests<TTest, TestUnityGame>(outputHelper)
{
    protected BepInExPluginInstaller PluginInstaller => Game.LibraryItemInstallers.OfType<BepInExPluginInstaller>().Single();

    protected BepInExPackInstaller PackInstaller => Game.LibraryItemInstallers.OfType<BepInExPackInstaller>().Single();

    /// <summary>Runs the pack installer without committing when it refuses; the targets are "Location:path".</summary>
    protected Task<(InstallerResult Result, string[] Targets)> TryInstallPack(LibraryArchive.ReadOnly archive)
        => TryInstall(PackInstaller, archive);
}

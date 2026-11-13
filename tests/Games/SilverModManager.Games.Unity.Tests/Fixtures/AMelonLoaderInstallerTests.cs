using NexusMods.Paths;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.Unity.MelonLoader;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Games.Unity.Tests.Fixtures;

/// <summary>Runs the MelonLoader installers on a test archive and reports where its files would land.</summary>
public abstract class AMelonLoaderInstallerTests<TTest>(ITestOutputHelper outputHelper) : AUnityLoaderInstallerTests<TTest, TestMelonLoaderUnityGame>(outputHelper)
{
    protected MelonLoaderPackInstaller PackInstaller => Game.LibraryItemInstallers.OfType<MelonLoaderPackInstaller>().Single();

    protected MelonLoaderModInstaller ModInstaller => Game.LibraryItemInstallers.OfType<MelonLoaderModInstaller>().Single();

    protected Task<(InstallerResult Result, string[] Targets)> TryInstallPack(LibraryArchive.ReadOnly archive) => TryInstall(PackInstaller, archive);

    protected Task<(InstallerResult Result, string[] Targets)> TryInstallMod(LibraryArchive.ReadOnly archive) => TryInstall(ModInstaller, archive);

    /// <summary>Makes the located game folder look like an install of the given scripting backend, the way the game ships it.</summary>
    protected void SetRuntime(UnityRuntime runtime)
    {
        var folder = GameInstallation.Locations[LocationId.Game].Path;
        var marker = runtime == UnityRuntime.Il2Cpp
            ? folder.Combine("GameAssembly.dll")
            : folder.Combine(TestMelonLoaderUnityGame.Definition.DataFolder).Combine("Managed/Assembly-CSharp.dll");
        marker.Parent.CreateDirectory();
        File.WriteAllText(marker.ToString(), "");
    }
}

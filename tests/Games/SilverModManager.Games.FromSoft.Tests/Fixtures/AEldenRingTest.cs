using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>Elden Ring installed from a Steam library, found by the real Steam locator.</summary>
public abstract class AEldenRingTest<TTest>(ITestOutputHelper helper, bool dirtyGameFolder = false)
    : ASteamInstalledGameTest<TTest, EldenRingGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
        => UseDefaultDataRoot ? base.AddServices(services).AddFromSoftGames() : base.AddServices(services).AddFromSoftGamesWithOwnDataRoot();

    /// <summary>True for a test of where the data root is by default; such a test must not sync, since the default is shared.</summary>
    protected virtual bool UseDefaultDataRoot => false;

    protected override SteamInstall InstallGame(SteamLibraryFixture library) => EldenRingInstall.Install(library, dirtyGameFolder);

    /// <summary>The <c>Game</c> subfolder of the install, which no sync may write to.</summary>
    protected AbsolutePath RealGameFolder => EldenRingPaths.GameFolder(GameInstallation);

    protected string[] SnapshotOfGameFolder() => TreeSnapshot.Take(RealGameFolder);
}

using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.TestFramework;
using NexusMods.Games.TestFramework.Helpers;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Tests.Fixtures;

/// <summary>Elden Ring installed from a Steam library, found by the real Steam locator.</summary>
public abstract class AEldenRingTest<TTest>(ITestOutputHelper helper, bool dirtyGameFolder = false)
    : ASteamInstalledGameTest<TTest, EldenRingGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddFromSoftGames();

    protected override SteamInstall InstallGame(SteamLibraryFixture library) => EldenRingInstall.Install(library, dirtyGameFolder);

    /// <summary>The <c>Game</c> subfolder of the install, which no sync may write to.</summary>
    protected AbsolutePath RealGameFolder => EldenRingPaths.GameFolder(GameInstallation);

    protected string[] SnapshotOfGameFolder() => TreeSnapshot.Take(RealGameFolder);
}

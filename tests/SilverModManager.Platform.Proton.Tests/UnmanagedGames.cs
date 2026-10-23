using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Platform.Proton.Tests;

/// <summary>
/// The deployment dependencies of a tool runner for tests that don't deploy anything: no installation has
/// metadata, so no plan is read and no folder is added to the container.
/// </summary>
internal static class UnmanagedGames
{
    public static IGameRegistry Registry()
    {
        var registry = Substitute.For<IGameRegistry>();
        registry.TryGetMetadata(Arg.Any<GameInstallation>(), out Arg.Any<GameInstallMetadata.ReadOnly>()).Returns(false);
        return registry;
    }

    public static DeploymentLaunchRoots LaunchRoots() => new(Substitute.For<IDeploymentPlanStore>(), [], Substitute.For<IConnection>());
}

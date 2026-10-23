using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Platform.Proton.Tests;

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

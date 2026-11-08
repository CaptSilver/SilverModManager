using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using System.Runtime.InteropServices;
using SilverModManager.Sdk.Settings;
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

    /// <summary>An x86-64 process, which can run Windows programs without reading anything.</summary>
    public static HostExecution X64Host() => new("/nonexistent/binfmt_misc", Architecture.X64);

    public static ISettingsManager Settings(bool assumeX86_64Runs = false)
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<HostExecutionSettings>(Arg.Any<string?>()).Returns(new HostExecutionSettings { AssumeX86_64Runs = assumeX86_64Runs });
        return settings;
    }

    public static DeploymentLaunchRoots LaunchRoots() => new(Substitute.For<IDeploymentPlanStore>(), [], Substitute.For<IConnection>());
}

using FluentAssertions;
using SilverModManager.Abstractions.Games.Starters;
using SilverModManager.Platform.Proton.Launch.Starters;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class LaunchServicesTests
{
    [Fact]
    public void AddHeadlessLaunch_RegistersALockFactoryRootedInTheRuntimeDir()
    {
        var env = Substitute.For<IEnvironmentReader>();
        env.Get("XDG_RUNTIME_DIR").Returns("/run/user/1000");

        var services = new ServiceCollection();
        services.AddSingleton(env);
        AddLaunchDependencies(services);
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var installationLock = provider.GetRequiredService<IInstallationLockFactory>().Create("steam", "1091500");

        installationLock.LockPath.Should().Be("/run/user/1000/silvermod/launch-steam-1091500.lock");
    }

    // The app registers both, and Validate() at startup refuses a service registered twice.
    [Fact]
    public void AddProtonAndAddHeadlessLaunch_RegisterEachServiceOnce()
    {
        var services = new ServiceCollection();
        services.AddProton();
        services.AddHeadlessLaunch();

        var act = () => services.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void AddGameLaunchCore_RegistersTheSelectorAndTheDirectAndProcessStartersOnceEach()
    {
        var services = new ServiceCollection();
        services.AddGameLaunchCore();
        services.AddGameLaunchCore();

        services.Count(d => d.ServiceType == typeof(IGameStarterSelector)).Should().Be(1);
        services.Where(d => d.ServiceType == typeof(IGameStarter)).Select(d => d.ImplementationType)
            .Should().BeEquivalentTo([typeof(DirectGameStarter), typeof(ProcessGameStarter)]);
    }

    [Fact]
    public void AddHeadlessLaunch_RegistersTheRunningGameGateOnceHoweverOftenItIsCalled()
    {
        var services = new ServiceCollection();
        services.AddHeadlessLaunch();
        services.AddHeadlessLaunch();

        services.Count(d => d.ServiceType == typeof(ILaunchGate) && d.ImplementationType == typeof(RunningGameGate)).Should().Be(1);
    }

    [Fact]
    public void AddHeadlessLaunch_RegistersTheMountGate()
    {
        var services = new ServiceCollection();
        services.AddHeadlessLaunch();

        services.Should().Contain(d => d.ServiceType == typeof(ILaunchGate) && d.ImplementationType == typeof(MountLaunchGate));
    }

    [Fact]
    public void Create_Throws_WhenThereIsNoRuntimeDir()
    {
        var env = Substitute.For<IEnvironmentReader>();
        env.Get("XDG_RUNTIME_DIR").Returns((string?)null);

        var services = new ServiceCollection();
        services.AddSingleton(env);
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IInstallationLockFactory>().Create("steam", "1");
        act.Should().Throw<InvalidOperationException>().WithMessage("*XDG_RUNTIME_DIR*");
    }

    [Fact]
    public void AddHeadlessLaunch_RegistersTheProcTracker()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEnvironmentReader>());
        AddLaunchDependencies(services);
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<IProcessTracker>().Should().BeOfType<ProcProcessTracker>();
    }

    [Fact]
    public void AddHeadlessLaunch_RegistersTheDesktopNotifier()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEnvironmentReader>());
        AddLaunchDependencies(services);
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<IDesktopNotifier>().Should().BeOfType<DesktopNotifier>();
    }

    [Fact]
    public void AddHeadlessLaunch_RegistersEverythingALaunchSessionNeeds()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEnvironmentReader>());
        AddLaunchDependencies(services);
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<LaunchSession>().Should().NotBeNull();
        provider.GetRequiredService<LaunchSession>().Should().NotBeSameAs(provider.GetRequiredService<LaunchSession>(), "each launch is its own session");
        provider.GetRequiredService<IGameProcessFactory>().Should().NotBeNull();
        provider.GetRequiredService<ILaunchSynchronizer>().Should().NotBeNull();
    }

    // The GUI starts its launches through the registry it gets from the container, so that one must be able to.
    [Fact]
    public void AddHeadlessLaunch_RegistersARegistryThatCanStartSessions()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEnvironmentReader>());
        AddLaunchDependencies(services);
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var registry = provider.GetRequiredService<ActiveLaunchSessions>();

        registry.CreateSession().Should().NotBeSameAs(registry.CreateSession(), "each launch is its own session");
    }

    [Fact]
    public void AddHeadlessLaunch_RegistersTheLoadoutResolver()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEnvironmentReader>());
        AddLaunchDependencies(services);
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<LoadoutResolver>().Should().BeSameAs(provider.GetRequiredService<LoadoutResolver>());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void TheProcessFactoryLeavesSignalForwardingToAStopHandlerWhenThereIsOne(bool withStopHandler, bool forwards)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEnvironmentReader>());
        AddLaunchDependencies(services);
        services.AddHeadlessLaunch();
        if (withStopHandler)
            services.AddSingleton(s => new StopHandler(s.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger(nameof(StopHandler))));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IGameProcessFactory>().Should().BeOfType<GameProcessFactory>()
            .Which.ForwardTermination.Should().Be(forwards);
    }

    [Fact]
    public async Task TheLaunchSynchronizer_RefusesASynchronizerServiceWithoutTheLaunchEntry()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEnvironmentReader>());
        AddLaunchDependencies(services);
        var other = Substitute.For<ISynchronizerService>();
        services.AddSingleton(other);
        services.AddHeadlessLaunch();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ILaunchSynchronizer>().Synchronize(LoadoutId.From(1));

        await act.Should().ThrowAsync<InvalidOperationException>();
        await other.DidNotReceiveWithAnyArgs().Synchronize(default);
    }

    private static void AddLaunchDependencies(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton(Substitute.For<IProcessRunner>());
        // What the app's data model and game registry provide to the loadout resolver.
        services.AddSingleton(Substitute.For<IConnection>());
        services.AddSingleton(Substitute.For<IGameRegistry>());
        // The app's file system, which the Steam launch option verb uses to find the launcher script.
        services.AddSingleton(Substitute.For<IFileSystem>());
        // The tool manager and job monitor `loadout launch` starts the game with, as the Play button does.
        services.AddSingleton(Substitute.For<IToolManager>());
        services.AddSingleton(Substitute.For<IJobMonitor>());
        // What the app's deployment services provide to the launch gate that mounts a plan's roots.
        services.AddSingleton(Substitute.For<IDeploymentMountScopeFactory>());
        services.AddSingleton(Substitute.For<IDeploymentPlanStore>());
        services.AddSingleton(Substitute.For<IDeploymentBackendSelector>());
        services.AddSingleton<DeploymentLaunchRoots>();
        // What the direct and process starters run the game with.
        services.AddSingleton(Substitute.For<IToolRunner>());
        services.AddSingleton(Substitute.For<IPrefixResolver>());
        services.AddSingleton<HeldMounts>();
    }
}

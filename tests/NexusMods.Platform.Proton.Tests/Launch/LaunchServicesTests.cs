using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NSubstitute;

namespace NexusMods.Platform.Proton.Tests.Launch;

public class LaunchServicesTests
{
    [Fact]
    public void AddHeadlessLaunch_RegistersALockFactoryRootedInTheRuntimeDir()
    {
        var env = Substitute.For<IEnvironmentReader>();
        env.Get("XDG_RUNTIME_DIR").Returns("/run/user/1000");

        var services = new ServiceCollection();
        services.AddSingleton(env);
        AddDesktopNotifierDependencies(services);
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
        AddDesktopNotifierDependencies(services);
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<IProcessTracker>().Should().BeOfType<ProcProcessTracker>();
    }

    [Fact]
    public void AddHeadlessLaunch_RegistersTheDesktopNotifier()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEnvironmentReader>());
        AddDesktopNotifierDependencies(services);
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<IDesktopNotifier>().Should().BeOfType<DesktopNotifier>();
    }

    [Fact]
    public void AddHeadlessLaunch_RegistersEverythingALaunchSessionNeeds()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEnvironmentReader>());
        AddDesktopNotifierDependencies(services);
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<LaunchSession>().Should().NotBeNull();
        provider.GetRequiredService<LaunchSession>().Should().NotBeSameAs(provider.GetRequiredService<LaunchSession>(), "each launch is its own session");
        provider.GetRequiredService<IGameProcessFactory>().Should().NotBeNull();
        provider.GetRequiredService<ILaunchSynchronizer>().Should().NotBeNull();
    }

    [Fact]
    public async Task TheLaunchSynchronizer_RefusesASynchronizerServiceWithoutTheLaunchEntry()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEnvironmentReader>());
        AddDesktopNotifierDependencies(services);
        var other = Substitute.For<ISynchronizerService>();
        services.AddSingleton(other);
        services.AddHeadlessLaunch();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ILaunchSynchronizer>().Synchronize(LoadoutId.From(1));

        await act.Should().ThrowAsync<InvalidOperationException>();
        await other.DidNotReceiveWithAnyArgs().Synchronize(default);
    }

    private static void AddDesktopNotifierDependencies(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton(Substitute.For<IProcessRunner>());
    }
}

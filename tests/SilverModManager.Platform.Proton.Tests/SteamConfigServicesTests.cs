using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests;

public class SteamConfigServicesTests
{
    private static readonly Type[] SharedServices =
    [
        typeof(IProcessTracker),
        typeof(IInstallationLockFactory),
        typeof(SteamConfigFileEditor),
        typeof(LocalConfigVdfWriter),
        typeof(ISteamLaunchOptionWriter),
        typeof(MountRecoveryFindings),
        typeof(ILaunchFinishMarkers),
    ];

    [Fact]
    public void AddSteamConfigAndLocks_RegistersEachSharedServiceOnce()
    {
        var services = new ServiceCollection().AddSteamConfigAndLocks();

        foreach (var type in SharedServices)
            services.Count(d => d.ServiceType == type).Should().Be(1, $"{type.Name} is registered once");
    }

    [Fact]
    public void AddSteamConfigAndLocks_CalledTwice_DoesNotRegisterAgain()
    {
        var services = new ServiceCollection().AddSteamConfigAndLocks();
        var before = services.Count;

        services.AddSteamConfigAndLocks();

        services.Count.Should().Be(before);
    }

    [Fact]
    public void AddProton_RegistersTheSharedServices()
    {
        var services = new ServiceCollection().AddProton();

        foreach (var type in SharedServices)
            services.Count(d => d.ServiceType == type).Should().Be(1, $"{type.Name} is registered once");
    }

    [Fact]
    public void AddHeadlessLaunch_RegistersTheSharedServices()
    {
        var services = new ServiceCollection().AddHeadlessLaunch();

        foreach (var type in SharedServices)
            services.Count(d => d.ServiceType == type).Should().Be(1, $"{type.Name} is registered once");
    }

    [Fact]
    public void AddProtonThenAddHeadlessLaunch_SharedServicesComeFromOneRegistration()
    {
        var shared = new ServiceCollection().AddSteamConfigAndLocks();
        var both = new ServiceCollection().AddProton().AddHeadlessLaunch();

        foreach (var type in SharedServices)
        {
            var expected = shared.Single(d => d.ServiceType == type);
            var actual = both.Single(d => d.ServiceType == type);
            actual.ImplementationType.Should().Be(expected.ImplementationType, type.Name);
            actual.Lifetime.Should().Be(expected.Lifetime, type.Name);
        }
    }
}

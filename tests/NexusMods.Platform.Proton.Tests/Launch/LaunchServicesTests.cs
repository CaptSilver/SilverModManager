using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Platform.Proton.Launch;
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
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var installationLock = provider.GetRequiredService<IInstallationLockFactory>().Create("steam", "1091500");

        installationLock.LockPath.Should().Be("/run/user/1000/silvermod/launch-steam-1091500.lock");
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
}

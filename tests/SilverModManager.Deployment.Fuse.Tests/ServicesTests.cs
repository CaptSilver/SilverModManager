using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Fuse.Availability;
using SilverModManager.Deployment.Fuse.Kernel;
using SilverModManager.Deployment.Fuse.Mount;
using SilverModManager.Deployment.Vfs;
using SilverModManager.Sdk;

namespace SilverModManager.Deployment.Fuse.Tests;

public class ServicesTests
{
    [Fact]
    public void AddFuseDeployment_RegistersTheKernelAndNoBackend()
    {
        var services = new ServiceCollection().AddFuseDeployment();

        services.Should().NotContain(d => d.ServiceType == typeof(IDeploymentBackend));
        services.Should().ContainSingle(d => d.ServiceType == typeof(IFuseKernel))
            .Which.ImplementationType.Should().Be(typeof(LibFuseLowLevelKernel));
    }

    [Fact]
    public void AddFuseDeployment_PassesTheDuplicateCheckEvenWhenCalledTwice()
    {
        var act = () => new ServiceCollection().AddFuseDeployment().AddFuseDeployment().Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void AddFuseDeployment_RegistersTheProbeAndUnmounter()
    {
        using var provider = new ServiceCollection().AddFuseDeployment().BuildServiceProvider();

        provider.GetRequiredService<IFuseEnvironmentProbe>().Should().BeOfType<FuseEnvironmentProbe>();
        provider.GetRequiredService<IFuseUnmounter>().Should().BeOfType<FusermountUnmounter>();
    }
}

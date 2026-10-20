using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Deployment.Fuse.Availability;
using NexusMods.Deployment.Fuse.Mount;
using NexusMods.Deployment.Vfs;
using NexusMods.Sdk;

namespace NexusMods.Deployment.Fuse.Tests;

public class ServicesTests
{
    [Fact]
    public void AddFuseDeployment_RegistersNoBackendAndNoKernel()
    {
        var services = new ServiceCollection().AddFuseDeployment();

        services.Should().NotContain(d => d.ServiceType == typeof(IDeploymentBackend));
        services.Should().NotContain(d => d.ServiceType == typeof(IFuseKernel));
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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Deployment.Fuse.Availability;
using NexusMods.Deployment.Fuse.Mount;

namespace NexusMods.Deployment.Fuse;

public static class Services
{
    /// <summary>
    /// Registers the FUSE preflight probe and the unmounter. It adds no <c>IDeploymentBackend</c> and no
    /// <c>IFuseKernel</c>: nothing mounts yet, and the default deployment plan keeps routing to the copy backend.
    /// </summary>
    public static IServiceCollection AddFuseDeployment(this IServiceCollection services)
    {
        services.TryAddSingleton<IFuseEnvironmentProbe, FuseEnvironmentProbe>();
        services.TryAddSingleton<IFusermountRunner, CliFusermountRunner>();
        services.TryAddSingleton<IFuseUnmounter, FusermountUnmounter>();
        return services;
    }
}

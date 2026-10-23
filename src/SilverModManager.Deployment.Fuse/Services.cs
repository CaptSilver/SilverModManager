using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Deployment.Fuse.Availability;
using SilverModManager.Deployment.Fuse.Kernel;
using SilverModManager.Deployment.Fuse.Mount;
using SilverModManager.Deployment.Vfs;

namespace SilverModManager.Deployment.Fuse;

public static class Services
{
    /// <summary>
    /// Registers the FUSE preflight probe, the unmounter and the libfuse kernel. It adds no
    /// <c>IDeploymentBackend</c> and nothing calls this method from the app, so no root can be routed to a mount.
    /// </summary>
    public static IServiceCollection AddFuseDeployment(this IServiceCollection services)
    {
        services.TryAddSingleton<IFuseEnvironmentProbe, FuseEnvironmentProbe>();
        services.TryAddSingleton<IFusermountRunner, CliFusermountRunner>();
        services.TryAddSingleton<IFuseUnmounter, FusermountUnmounter>();
        services.TryAddSingleton<IFuseKernel, LibFuseLowLevelKernel>();
        return services;
    }
}

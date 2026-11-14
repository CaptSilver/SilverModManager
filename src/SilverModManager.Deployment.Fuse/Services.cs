using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Fuse.Availability;
using SilverModManager.Deployment.Fuse.Backend;
using SilverModManager.Deployment.Fuse.Kernel;
using SilverModManager.Deployment.Fuse.Mount;
using SilverModManager.Deployment.Vfs;

namespace SilverModManager.Deployment.Fuse;

public static class Services
{
    /// <summary>
    /// Registers the fuse deployment backend with the FUSE preflight probe, the unmounter and the libfuse kernel.
    /// The chain never picks the backend (<c>DeploymentBackendChain.Order</c> does not list it): a person chooses it for
    /// one root with <c>silvermodmanager deployment --use fuse</c>.
    /// </summary>
    public static IServiceCollection AddFuseDeployment(this IServiceCollection services)
    {
        services.TryAddSingleton<IFuseEnvironmentProbe, FuseEnvironmentProbe>();
        services.TryAddSingleton<IFusermountRunner, CliFusermountRunner>();
        services.TryAddSingleton<IFuseUnmounter, FusermountUnmounter>();
        services.TryAddSingleton<IFuseKernel, LibFuseLowLevelKernel>();
        // Two type arguments: with only IDeploymentBackend the implementation type is inferred as the interface and
        // TryAddEnumerable refuses the descriptor.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDeploymentBackend, FuseDeploymentBackend>());
        return services;
    }
}

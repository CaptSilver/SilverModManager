using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Deployment.Links.Backends;

namespace NexusMods.Deployment.Links;

public static class Services
{
    /// <summary>
    /// Registers the reflink, hardlink and symlink deployment backends. They need the copy backend and the
    /// loose staging store, which <c>AddDeploymentBackends</c> and <c>AddLooseStagingStore</c> register.
    /// </summary>
    public static IServiceCollection AddLinkDeploymentBackends(this IServiceCollection s)
    {
        s.TryAddSingleton<ReflinkDeploymentBackend>();
        s.TryAddSingleton<HardlinkDeploymentBackend>();
        s.TryAddSingleton<SymlinkDeploymentBackend>();

        // Two type arguments: with only IDeploymentBackend the implementation type is inferred as the
        // interface and TryAddEnumerable refuses the descriptor.
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IDeploymentBackend, ReflinkDeploymentBackend>(sp => sp.GetRequiredService<ReflinkDeploymentBackend>()));
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IDeploymentBackend, HardlinkDeploymentBackend>(sp => sp.GetRequiredService<HardlinkDeploymentBackend>()));
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IDeploymentBackend, SymlinkDeploymentBackend>(sp => sp.GetRequiredService<SymlinkDeploymentBackend>()));
        return s;
    }
}

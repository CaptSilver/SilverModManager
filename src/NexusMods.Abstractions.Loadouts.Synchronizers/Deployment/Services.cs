using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Loadouts.Deployment;

namespace NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;

public static class Services
{
    /// <summary>
    /// Registers the deployment plan model and store, the backend selection chain, and the copy backend. The model is added here rather than with the
    /// loadout abstractions so that hosts that build the data model without them still know its attributes.
    /// </summary>
    public static IServiceCollection AddDeploymentBackends(this IServiceCollection s)
    {
        s.AddDeploymentPlanEntryModel();
        s.TryAddSingleton<IDeploymentPlanStore, DeploymentPlanStore>();
        s.TryAddSingleton<IDeploymentBackendSelector, DeploymentBackendChain>();
        s.TryAddSingleton<CopyDeploymentBackend>();
        // Two type arguments: with only IDeploymentBackend the implementation type is inferred as the
        // interface and TryAddEnumerable refuses the descriptor.
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IDeploymentBackend, CopyDeploymentBackend>(sp => sp.GetRequiredService<CopyDeploymentBackend>()));
        return s;
    }
}

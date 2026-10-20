using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Loadouts.Deployment;

namespace NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;

public static class Services
{
    /// <summary>
    /// Registers the deployment plan model and store. The model is added here rather than with the
    /// loadout abstractions so that hosts that build the data model without them still know its attributes.
    /// </summary>
    public static IServiceCollection AddDeploymentBackends(this IServiceCollection s)
    {
        s.AddDeploymentPlanEntryModel();
        s.TryAddSingleton<IDeploymentPlanStore, DeploymentPlanStore>();
        return s;
    }
}

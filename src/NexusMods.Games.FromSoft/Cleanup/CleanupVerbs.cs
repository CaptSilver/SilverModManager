using Microsoft.Extensions.DependencyInjection;
using NexusMods.Platform.Proton.Doctor;

namespace NexusMods.Games.FromSoft.Cleanup;

internal static class CleanupVerbs
{
    internal static IServiceCollection AddCleanupVerbs(this IServiceCollection services)
    {
        services.AddSingleton<CleanupService>();
        services.AddSingleton<CleanupCommand>();
        services.AddSingleton<IGameCleanupGuide, EldenRingCleanupGuide>();
        services.AddSingleton<IImmutableFlagProbe, IoctlImmutableFlagProbe>();
        return services;
    }
}

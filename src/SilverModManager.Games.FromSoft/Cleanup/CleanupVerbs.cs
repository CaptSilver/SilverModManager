using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Platform.Proton.Doctor;

namespace SilverModManager.Games.FromSoft.Cleanup;

internal static class CleanupVerbs
{
    internal static IServiceCollection AddCleanupVerbs(this IServiceCollection services)
    {
        services.AddSingleton<CleanupService>();
        services.AddSingleton<CleanupCommand>();
        services.AddSingleton<IGameCleanupGuide, FromSoftCleanupGuide>();
        services.AddSingleton<IImmutableFlagProbe, IoctlImmutableFlagProbe>();
        return services;
    }
}

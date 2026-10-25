using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.Platform.Proton.Doctor;

namespace SilverModManager.Games.FromSoft.Cleanup;

internal static class CleanupVerbs
{
    internal static IServiceCollection AddCleanupVerbs(this IServiceCollection services)
    {
        services.AddSingleton<CleanupService>();
        services.AddSingleton<ICleanupFixService>(sp => sp.GetRequiredService<CleanupService>());
        services.AddSingleton<ILibrarySaver, LibrarySaver>();
        services.AddSingleton<ElevatedCommandRunner>();
        services.AddSingleton<CleanupCommand>();
        services.AddSingleton<IGameCleanupGuide, FromSoftCleanupGuide>();
        services.AddSingleton<IImmutableFlagProbe, IoctlImmutableFlagProbe>();
        return services;
    }
}

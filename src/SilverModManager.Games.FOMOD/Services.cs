using FomodInstaller.Interface;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.FOMOD.CoreDelegates;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Games.FOMOD;

public static class Services
{
    /// <summary>
    /// Adds FOMOD [currently XML only] support to your DI container.
    /// </summary>
    /// <param name="services">The services to add.</param>
    /// <returns>Collection of services.</returns>
    public static IServiceCollection AddFomod(this IServiceCollection services)
    {
        services.AddAttributeCollection(typeof(EmptyDirectory));
        services.AddTransient<ICoreDelegates, InstallerDelegates>();
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Secrets;
using NexusMods.Abstractions.Steam;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.Networking.Steam.CLI;
using NexusMods.Networking.Steam.Migration;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Networking.Steam;

public static class Services
{
    /// <summary>
    /// Add the steam store DI systems to the container
    /// </summary>
    public static IServiceCollection AddSteam(this IServiceCollection services)
    {
        services.AddSingleton<ISteamSession, Session>();
        services.TryAddSingleton<IDepotManifestSource, LocalDepotManifests>();
        return services;
    }
    
    /// <summary>
    /// Adds a logging authentication handler to the DI container
    /// </summary>
    public static IServiceCollection AddLoggingAuthenticationHandler(this IServiceCollection services)
    {
        services.AddSingleton<IAuthInterventionHandler, LoggingAuthInterventionHandler>();
        return services;
    }
    
    /// <summary>
    /// Adds auth storage to the DI container that keeps the auth data in the secret store, and moves
    /// a login from the file older versions used into it.
    /// </summary>
    public static IServiceCollection AddSecretStoreAuthStorage(this IServiceCollection services)
    {
        services.AddSecretKeys(SteamSecretKeys.Auth);
        services.AddSingleton<IAuthStorage, SecretStoreAuthStorage>();
        services.AddSingleton<ISecretMigrationStep, SteamAuthFileMigration>();
        return services;
    }
    
    public static IServiceCollection AddSteamCli(this IServiceCollection services)
    {
        services.AddOptionParser<AppId>(s =>
            {
                if (uint.TryParse(s, out var parsed))
                    return (AppId.From(parsed), null);
                return (default(AppId), "Invalid AppId");
            }
        );
        services.AddSteam();
        services.AddSingleton<IAuthInterventionHandler, RenderingAuthenticationHandler>();
        services.AddSecretStoreAuthStorage();
        services.AddSteamVerbs();
        return services;
    }
    
}

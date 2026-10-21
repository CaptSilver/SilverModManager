using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games.Declarative;
using NexusMods.Games.Declarative.Capabilities;
using NexusMods.Games.Declarative.Catalog;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Declarative;

public static class Services
{
    /// <summary>
    /// Adds the capability registry and the definition catalog. The catalog is built on first use, so
    /// capabilities registered by other projects are known by then.
    /// </summary>
    /// <param name="loadUserDefinitions">Whether to read the user's definitions; only the app host passes true.</param>
    public static IServiceCollection AddDeclarativeGames(this IServiceCollection services, IFileSystem fileSystem, bool loadUserDefinitions = false)
    {
        services.AddSingleton<ICapabilityRegistry>(provider => new CapabilityRegistry(provider.GetServices<IGameCapability>()));
        services.AddSingleton(provider => new GameDefinitionCatalog(
            fileSystem,
            provider.GetRequiredService<ICapabilityRegistry>(),
            provider.GetRequiredService<ILogger<GameDefinitionCatalog>>(),
            loadUserDefinitions,
            codedGames: () => provider.GetServices<IGameData>()
        ));
        services.AddSingleton<IGameDefinitionCatalog>(provider => provider.GetRequiredService<GameDefinitionCatalog>());
        return services;
    }
}

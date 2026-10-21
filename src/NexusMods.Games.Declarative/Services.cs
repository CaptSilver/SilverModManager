using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Games.Declarative;
using NexusMods.Games.Declarative.Capabilities;
using NexusMods.Games.Declarative.Catalog;
using NexusMods.Games.Declarative.Schema;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Declarative;

public static class Services
{
    /// <summary>
    /// Loads the game definitions and registers one <see cref="DeclarativeGame"/> for each, as both
    /// <see cref="IGame"/> and <see cref="IGameData"/>, plus the capability registry and the catalog.
    /// </summary>
    /// <remarks>
    /// The definitions are read right here, because a game is registered per definition. Capabilities
    /// are needed to validate them, so every <see cref="IGameCapability"/> has to be registered before
    /// this call, as an instance or by a type with a public parameterless constructor; a capability
    /// that needs services gets them through <see cref="IGameCapability.Attach"/>.
    /// </remarks>
    /// <param name="loadUserDefinitions">Whether to read the user's definitions; only the app host passes true.</param>
    /// <param name="additionalBundled">Definitions loaded as if embedded, for tests.</param>
    public static IServiceCollection AddDeclarativeGames(
        this IServiceCollection services,
        IFileSystem fileSystem,
        bool loadUserDefinitions = false,
        IEnumerable<DefinitionSource>? additionalBundled = null)
    {
        var registry = new CapabilityRegistry(RegisteredCapabilities(services));
        var logger = new BufferedLogger<GameDefinitionCatalog>();
        IServiceProvider? container = null;
        var catalog = new GameDefinitionCatalog(
            fileSystem,
            registry,
            logger,
            loadUserDefinitions,
            additionalBundled,
            codedGames: () => (container ?? throw new InvalidOperationException("The game definition catalog was used before the service provider was built")).GetServices<IGameData>()
        );

        services.AddSingleton<ICapabilityRegistry>(registry);
        services.AddSingleton(provider =>
        {
            container = provider;
            logger.ReplayTo(provider.GetRequiredService<ILogger<GameDefinitionCatalog>>());
            return catalog;
        });
        services.AddSingleton<IGameDefinitionCatalog>(provider => provider.GetRequiredService<GameDefinitionCatalog>());

        foreach (var loaded in catalog.Definitions)
        {
            var definition = loaded.Definition;
            services.AddKeyedSingleton(definition.Id, (provider, _) =>
            {
                // Resolving the catalog is what replays what it logged while loading.
                _ = provider.GetRequiredService<GameDefinitionCatalog>();
                return new DeclarativeGame(provider, definition);
            });
            services.AddSingleton<IGame>(provider => provider.GetRequiredKeyedService<DeclarativeGame>(definition.Id));
            services.AddSingleton<IGameData>(provider => provider.GetRequiredKeyedService<DeclarativeGame>(definition.Id));
        }

        return services;
    }

    private static IEnumerable<IGameCapability> RegisteredCapabilities(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IGameCapability) && !descriptor.IsKeyedService))
        {
            if (descriptor.ImplementationInstance is IGameCapability instance)
            {
                yield return instance;
            }
            else if (descriptor.ImplementationType is { } type)
            {
                yield return Create(type);
            }
            else
            {
                throw new InvalidOperationException("An IGameCapability registered through a factory can't be read while services are being registered; register it as an instance or by type");
            }
        }

        static IGameCapability Create(Type type)
        {
            try
            {
                return Activator.CreateInstance(type) as IGameCapability ?? throw new InvalidOperationException($"{type} did not create an IGameCapability");
            }
            catch (MissingMethodException e)
            {
                throw new InvalidOperationException($"{type} needs a public parameterless constructor to be registered as an IGameCapability before the game definitions load", e);
            }
        }
    }
}

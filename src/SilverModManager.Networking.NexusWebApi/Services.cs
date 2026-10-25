using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.NexusWebApi.Auth;
using SilverModManager.Networking.NexusWebApi.Migration;
using SilverModManager.Networking.NexusWebApi.UpdateFilters;
using SilverModManager.Networking.NexusWebApi.V1Interop;
using SilverModManager.Sdk;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>
/// Helps with registration of services for Microsoft DI container.
/// </summary>
public static class Services
{
    /// <summary>
    /// Adds the Nexus Web API to your DI Container's service collection.
    /// </summary>
    /// <param name="registerNxmHandler">
    /// Whether the process claims the nxm:// scheme at startup. A process that only runs a verb or a game
    /// launch must not touch the desktop's link handlers.
    /// </param>
    public static IServiceCollection AddNexusWebApi(this IServiceCollection collection, bool registerNxmHandler = true)
    {
        collection.AddLoginVerbs();

        // Both credential kinds are always registered; the routed factory picks one per call, so a
        // key or token stored while the app runs takes effect without a restart.
        collection
            .AddSingleton<ApiKeyMessageFactory>()
            .AddSingleton<OAuth2MessageFactory>()
            .AddAllSingleton<IHttpMessageFactory, CredentialRoutedMessageFactory>()
            .AddSingleton<IAuthenticatingMessageFactory>(sp => sp.GetRequiredService<CredentialRoutedMessageFactory>());
        collection.AddSingleton<OAuth>();

        // Headless fallback. A UI registers its own prompt first and TryAdd leaves it alone.
        collection.TryAddSingleton<IApiKeyPrompt, NoInteractiveApiKeyPrompt>();
        collection.AddSingleton<IIDGenerator, IDGenerator>();

        // The models stay registered so the migration steps can find and excise the rows older versions wrote.
        collection.AddJWTTokenModel();
        collection.AddApiKeyModel();

        collection
            .AddSecretKeys(NexusSecretKeys.All)
            .AddSingleton<ISecretMigrationStep, NexusApiKeyMigration>()
            .AddSingleton<ISecretMigrationStep, NexusOAuthTokenMigration>()
            .AddSingleton<NexusCredentialCache>()
            .AddSingleton<ISecretPrimable>(sp => sp.GetRequiredService<NexusCredentialCache>())
            .AddSingleton<INexusCredentialSummary>(sp => sp.GetRequiredService<NexusCredentialCache>());
        collection.AddSingleton(TimeProvider.System);
        collection.AddIgnoreFileUpdateModel();

        collection.AddGameDomainToGameIdMappingModel();
        collection.AddSingleton<GameDomainToGameIdMappingCache>();
        collection.AddSingleton<IGameDomainToGameIdMappingCache>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<LocalMappingCache>>();

            // TryParseJsonFile logs why parsing failed; this logs what it costs. Every lookup then
            // goes through the fallback cache, which is slower but correct, so the app stays usable.
            if (LocalMappingCache.TryParseJsonFile(logger, out var gameIdToDomain, out var gameDomainToId))
                logger.LogInformation("Loaded local mapping cache data with {Count} entries", gameDomainToId.Count);
            else logger.LogError("Failed to parse local mapping cache data, all mapping requests will be handled by the fallback cache");

            var fallbackCache = serviceProvider.GetRequiredService<GameDomainToGameIdMappingCache>();
            return new LocalMappingCache(logger, gameIdToDomain ?? FrozenDictionary<NexusModsGameId, GameDomain>.Empty, gameDomainToId ?? FrozenDictionary<GameDomain, NexusModsGameId>.Empty, fallbackCache);
        });

        collection
            .AddNexusModsLibraryModels()
            .AddSingleton<IGraphQlClient, GraphQlClient>()
            .AddSingleton<NexusModsLibrary>()
            .AddSingleton<INexusModsBrowser, NexusModsBrowser>()
            .AddSingleton<INexusCollectionsBrowser, NexusCollectionsBrowser>()
            .AddAllSingleton<ILoginManager, LoginManager>()
            .AddAllSingleton<INexusApiClient, NexusApiClient>()
            .AddSingleton<IModUpdateFilterService, ModUpdateFilterService>()
            .AddAllSingleton<IModUpdateService, ModUpdateService>()
            .AddSettings<NxmHandlerSettings>()
            .AddSettings<NexusAuthSettings>()
            .AddNexusApiVerbs();

        if (registerNxmHandler) collection.AddHostedService<HandlerRegistration>();

        collection.AddTransient<NexusAuthHandler>();
        collection
            .AddNexusGraphQLClient()
            .ConfigureHttpClient((serviceProvider, httpClient) =>
            {
                httpClient.BaseAddress = ClientConfig.GraphQlEndpoint;
                httpClient.DefaultRequestHeaders.UserAgent.Add(ApplicationConstants.UserAgent);

                httpClient.DefaultRequestHeaders.Add(BaseHttpMessageFactory.HeaderApplicationName, ApplicationConstants.UserAgent.ApplicationName);
                httpClient.DefaultRequestHeaders.Add(BaseHttpMessageFactory.HeaderApplicationVersion, ApplicationConstants.UserAgent.ApplicationVersion);
            }, clientBuilder => clientBuilder.AddHttpMessageHandler<NexusAuthHandler>());

        return collection;
    }
}

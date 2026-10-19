using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Networking.NexusWebApi.UpdateFilters;
using NexusMods.Networking.NexusWebApi.V1Interop;
using NexusMods.Sdk;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Sdk.Settings;

namespace NexusMods.Networking.NexusWebApi;

/// <summary>
/// Helps with registration of services for Microsoft DI container.
/// </summary>
public static class Services
{
    /// <summary>
    /// Adds the Nexus Web API to your DI Container's service collection.
    /// </summary>
    public static IServiceCollection AddNexusWebApi(this IServiceCollection collection)
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
        collection.AddSingleton<IIDGenerator, IDGenerator>();

        collection.AddJWTTokenModel();
        collection.AddApiKeyModel();
        collection.AddSingleton(TimeProvider.System);
        collection.AddIgnoreFileUpdateModel();

        collection.AddGameDomainToGameIdMappingModel();
        collection.AddSingleton<GameDomainToGameIdMappingCache>();
        collection.AddSingleton<IGameDomainToGameIdMappingCache>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<LocalMappingCache>>();

            // TryParseJsonFile logs why parsing failed; this logs what it costs. Every lookup then
            // goes through the fallback cache, which is slower but correct, so the app stays usable.
            var didParse = LocalMappingCache.TryParseJsonFile(logger, out var gameIdToDomain, out var gameDomainToId);
            if (didParse) logger.LogInformation("Loaded local mapping cache data with {Count} entries", gameDomainToId!.Count);
            else logger.LogError("Failed to parse local mapping cache data, all mapping requests will be handled by the fallback cache");

            var fallbackCache = serviceProvider.GetRequiredService<GameDomainToGameIdMappingCache>();
            return new LocalMappingCache(logger, gameIdToDomain ?? FrozenDictionary<NexusModsGameId, GameDomain>.Empty, gameDomainToId ?? FrozenDictionary<GameDomain, NexusModsGameId>.Empty, fallbackCache);
        });

        collection
            .AddNexusModsLibraryModels()
            .AddSingleton<IGraphQlClient, GraphQlClient>()
            .AddSingleton<NexusModsLibrary>()
            .AddAllSingleton<ILoginManager, LoginManager>()
            .AddAllSingleton<INexusApiClient, NexusApiClient>()
            .AddSingleton<IModUpdateFilterService, ModUpdateFilterService>()
            .AddAllSingleton<IModUpdateService, ModUpdateService>()
            .AddHostedService<HandlerRegistration>()
            .AddSettings<NxmHandlerSettings>()
            .AddSettings<NexusAuthSettings>()
            .AddNexusApiVerbs();

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

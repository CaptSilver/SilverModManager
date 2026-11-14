using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.DataModel.SchemaVersions;
using SilverModManager.Library.GameBanana;
using SilverModManager.Library.GitHub;
using SilverModManager.Library.Inference;
using SilverModManager.Library.Listing;
using SilverModManager.Library.Lock;
using SilverModManager.Library.Migrations;
using SilverModManager.Library.Pickup;
using SilverModManager.Library.Verbs;
using SilverModManager.Library.Workshop;
using SilverModManager.Networking.Steam.Workshop;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library;

/// <summary>
/// Extension methods.
/// </summary>
[PublicAPI]
public static class Services
{
    /// <summary>
    /// Extension method.
    /// </summary>
    public static IServiceCollection AddLibrary(this IServiceCollection serviceCollection)
    {
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.TryAddEnumerable(ServiceDescriptor.Singleton<IDestinationInference, LoadoutLinkInference>());
        serviceCollection.TryAddEnumerable(ServiceDescriptor.Singleton<IDestinationInference, ThunderstoreCommunityInference>());
        serviceCollection.TryAddEnumerable(ServiceDescriptor.Singleton<ILibraryItemKind, NexusModFileKind>());
        serviceCollection.TryAddEnumerable(ServiceDescriptor.Singleton<ILibraryItemKind, DownloadedFileKind>());
        serviceCollection.TryAddEnumerable(ServiceDescriptor.Singleton<ILibraryItemKind, LocalFileKind>());
        serviceCollection.TryAddEnumerable(ServiceDescriptor.Singleton<ILibraryItemKind, GitHubReleaseKind>());
        serviceCollection.TryAddEnumerable(ServiceDescriptor.Singleton<ILibraryItemKind, GameBananaKind>());
        serviceCollection.TryAddEnumerable(ServiceDescriptor.Singleton<ILibraryItemKind, ThunderstorePackageKind>());
        return serviceCollection
            .AddMigration<_0006_AssignLibraryDestinations>()
            .AddSingleton<ILibraryService, LibraryService>()
            .AddSingleton<IDownloadsService, DownloadsService>()
            .AddSingleton<LibraryFileDigests>()
            .AddTransient<LibraryListing>()
            .AddSingleton<IGitHubReleaseAssetSource, GitHubReleaseAssetSource>()
            .AddSingleton<IThunderstoreSource, ThunderstoreSource>()
            .AddSingleton<ILoadoutInstallCore, LoadoutInstallCore>()
            .AddSingleton<IThunderstoreInstaller, ThunderstoreInstaller>()
            .AddSingleton<INexusModsInstaller, NexusModsInstaller>()
            .AddSingleton<INexusUpdateDownloader, NexusUpdateDownloader>()
            .AddSingleton<IModSource, NexusModSource>()
            .AddSingleton<IModSource, ThunderstoreModSource>()
            .AddSingleton<IModSource>(provider => ActivatorUtilities.CreateInstance<GitHubModSource>(provider, GitHubClients.Api(provider), GitHubClients.Http(provider)))
            .AddSingleton(provider => ActivatorUtilities.CreateInstance<GameBananaProfiles>(provider, GameBananaClients.Api(provider)))
            .AddSingleton<IModSource>(provider => ActivatorUtilities.CreateInstance<GameBananaModSource>(provider, GameBananaClients.Http(provider)))
            .AddSingleton(SteamWorkshopSettings.Default)
            .AddSingleton(provider => ActivatorUtilities.CreateInstance<WorkshopItemsView>(provider, SteamWorkshopClients.Api(provider)))
            .AddSingleton<IModSource, SteamWorkshopSource>()
            .AddSingleton<GitHubShelfEntries>()
            .AddSingleton(provider => ActivatorUtilities.CreateInstance<GitHubReleases>(provider, GitHubClients.Api(provider)))
            .AddSingleton<IModCatalog, NexusModCatalog>()
            .AddSingleton<IModCatalog, ThunderstoreModCatalog>()
            .AddSingleton(provider => ActivatorUtilities.CreateInstance<GitHubShelfCatalog>(provider, GitHubClients.Api(provider), GitHubClients.Http(provider)))
            .AddSingleton<IModCatalog>(provider => provider.GetRequiredService<GitHubShelfCatalog>())
            .AddSingleton(provider => ActivatorUtilities.CreateInstance<GameBananaCatalog>(provider, GameBananaClients.Api(provider), GameBananaClients.Http(provider)))
            .AddSingleton<IModCatalog>(provider => provider.GetRequiredService<GameBananaCatalog>())
            .AddSingleton<IGameBananaInstaller>(provider => provider.GetRequiredService<GameBananaCatalog>())
            .AddSingleton(provider => ActivatorUtilities.CreateInstance<SteamWorkshopCatalog>(provider, SteamWorkshopClients.Api(provider)))
            .AddSingleton<IModCatalog>(provider => provider.GetRequiredService<SteamWorkshopCatalog>())
            .AddSingleton<ModCatalogs>()
            .AddSingleton<ModSourceResolver>()
            .AddSingleton<ExpectedDownloads>()
            .AddSingleton<IExpectedDownloads>(provider => provider.GetRequiredService<ExpectedDownloads>())
            .AddLockVerbs()
            .AddThunderstoreVerbs()
            .AddCatalogVerbs()
            .AddGitHubVerbs()
            .AddGameBananaVerbs()
            .AddWorkshopVerbs()
            .AddNexusModsVerbs();
    }
}

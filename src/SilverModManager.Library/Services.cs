using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.Library;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.DataModel.SchemaVersions;
using SilverModManager.Library.Inference;
using SilverModManager.Library.Lock;
using SilverModManager.Library.Migrations;
using SilverModManager.Library.Verbs;
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
        serviceCollection.TryAddEnumerable(ServiceDescriptor.Singleton<IDestinationInference, LoadoutLinkInference>());
        serviceCollection.TryAddEnumerable(ServiceDescriptor.Singleton<IDestinationInference, ThunderstoreCommunityInference>());
        return serviceCollection
            .AddMigration<_0006_AssignLibraryDestinations>()
            .AddSingleton<ILibraryService, LibraryService>()
            .AddSingleton<IDownloadsService, DownloadsService>()
            .AddSingleton<LibraryFileDigests>()
            .AddSingleton<IGitHubReleaseAssetSource, GitHubReleaseAssetSource>()
            .AddSingleton<IThunderstoreSource, ThunderstoreSource>()
            .AddLockVerbs()
            .AddThunderstoreVerbs();
    }
}

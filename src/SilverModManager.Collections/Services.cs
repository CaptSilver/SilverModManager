using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Collections;

public static class Services
{
    public static IServiceCollection AddNexusModsCollections(this IServiceCollection services)
    {
        return services
            .AddManagedCollectionLoadoutGroupModel()
            .AddNexusCollectionLoadoutGroupModel()
            .AddDirectDownloadLibraryFileModel()
            .AddNexusCollectionBundledLoadoutGroupModel()
            .AddNexusCollectionItemLoadoutGroupModel()
            .AddNexusCollectionReplicatedLoadoutGroupModel()
            .AddCollectionVerbs()
            .AddSingleton<CollectionDownloader>()
            .AddSettings<DownloadSettings>();
    }
}

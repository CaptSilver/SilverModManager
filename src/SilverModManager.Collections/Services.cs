using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Collections;

public static class Services
{
    public static IServiceCollection AddNexusModsCollections(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticEmitter, UnusedCollectionRuleEmitter>());

        return services
            .AddManagedCollectionLoadoutGroupModel()
            .AddNexusCollectionLoadoutGroupModel()
            .AddDirectDownloadLibraryFileModel()
            .AddNexusCollectionBundledLoadoutGroupModel()
            .AddNexusCollectionItemLoadoutGroupModel()
            .AddNexusCollectionReplicatedLoadoutGroupModel()
            .AddCollectionVerbs()
            .AddSingleton<CollectionDownloader>()
            .AddDownloadSettings();
    }
}

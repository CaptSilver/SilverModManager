using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;

namespace NexusMods.Sdk.Library;

/// <summary>
/// Extension methods.
/// </summary>
[PublicAPI]
public static class Services
{
    /// <summary>
    /// Extension method.
    /// </summary>
    public static IServiceCollection AddLibraryModels(this IServiceCollection serviceCollection)
    {
        return serviceCollection
            .AddLibraryItemModel()
            .AddLibraryFileModel()
            .AddDownloadedFileModel()
            .AddGitHubReleaseLibraryFileModel()
            .AddThunderstoreLibraryFileModel()
            .AddUpdatePinModel()
            .AddLocalFileModel()
            .AddLibraryArchiveModel()
            .AddLibraryArchiveFileEntryModel()
            .AddManuallyCreatedArchiveModel();
    }
}

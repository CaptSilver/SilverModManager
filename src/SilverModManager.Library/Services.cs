using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.Library;
using SilverModManager.Library.Lock;
using SilverModManager.Library.Verbs;

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
        return serviceCollection
            .AddSingleton<ILibraryService, LibraryService>()
            .AddSingleton<IDownloadsService, DownloadsService>()
            .AddSingleton<LibraryFileDigests>()
            .AddSingleton<IGitHubReleaseAssetSource, GitHubReleaseAssetSource>()
            .AddSingleton<IThunderstoreSource, ThunderstoreSource>()
            .AddLockVerbs()
            .AddThunderstoreVerbs();
    }
}

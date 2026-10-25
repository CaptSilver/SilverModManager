using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Cli;
using SilverModManager.Wabbajack.Diagnostics;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack;

[PublicAPI]
public static class Services
{
    public static IServiceCollection AddWabbajack(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticEmitter, WabbajackSkippedFilesEmitter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WabbajackModlistRemovalWatcher>());
        return services
            .AddWabbajackModlistLibraryFileModel()
            .AddWabbajackModlistLoadoutGroupModel()
            .AddWabbajackSkippedFileModel()
            .AddWabbajackSettings()
            .AddWabbajackVerbs()
            .AddSingleton<WabbajackPaths>()
            .AddSingleton<ILibraryArchiveLocator, LibraryArchiveLocator>()
            .AddSingleton<ArchiveInventory>()
            .AddSingleton<IDirectiveTransforms, DirectiveTransforms>()
            .AddSingleton<INestedArchiveOpener, BsaMemberOpener>()
            .AddSingleton<ExpectedNexusDownloads>()
            .AddSingleton<IExpectedNexusDownloads>(provider => provider.GetRequiredService<ExpectedNexusDownloads>())
            .AddSingleton<IArchiveDownloadJobs, ArchiveDownloadJobs>()
            .AddSingleton<IArchiveAcquirer, ArchiveAcquirer>()
            .AddSingleton<ArchiveInbox>()
            .AddSingleton<IModlistDownloadJobs, ModlistDownloadJobs>()
            .AddSingleton(provider => new WabbajackGalleryDownloader(
                provider.GetRequiredService<IModlistDownloadJobs>(),
                provider.GetRequiredService<ILibraryService>(),
                provider.GetRequiredService<IConnection>(),
                provider.GetRequiredService<TemporaryFileManager>()))
            .AddSingleton(provider => new WabbajackGalleryClient(
                provider.GetRequiredService<HttpClient>(),
                provider.GetRequiredService<WabbajackPaths>().GalleryCache.Combine("gallery.json"),
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                provider.GetRequiredService<ILogger<WabbajackGalleryClient>>()))
            .AddSingleton<ILibraryFileRecogniser, WabbajackModlistRecogniser>();
    }

    public static IServiceCollection AddWabbajackSettings(this IServiceCollection services)
    {
        return services.AddSettings<WabbajackSettings>();
    }
}

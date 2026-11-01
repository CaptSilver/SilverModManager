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
using SilverModManager.DataModel.SchemaVersions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Cli;
using SilverModManager.Wabbajack.Diagnostics;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Migrations;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack;

[PublicAPI]
public static class Services
{
    public static IServiceCollection AddWabbajack(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticEmitter, WabbajackSkippedFilesEmitter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WabbajackModlistRemovalWatcher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDestinationInference, ModlistFetchInference>());
        return services
            .AddWabbajackModlistLibraryFileModel()
            .AddWabbajackModlistArchiveModel()
            .AddMigration<_0005_LinkModlistArchives>()
            .AddWabbajackModlistLoadoutGroupModel()
            .AddWabbajackSkippedFileModel()
            .AddWabbajackSettings()
            .AddWabbajackVerbs()
            .AddSingleton<WabbajackPaths>()
            .AddSingleton<ILibraryArchiveLocator, LibraryArchiveLocator>()
            .AddSingleton<IModlistCatalog, ModlistCatalog>()
            .AddSingleton<ArchiveInventory>()
            .AddSingleton<IDirectiveTransforms, DirectiveTransforms>()
            .AddSingleton<INestedArchiveOpener, BsaMemberOpener>()
            .AddSingleton<ModlistPlanner>()
            .AddSingleton<IModlistPlanner>(provider => provider.GetRequiredService<ModlistPlanner>())
            .AddSingleton<ExpectedNexusDownloads>()
            .AddSingleton<IExpectedNexusDownloads>(provider => provider.GetRequiredService<ExpectedNexusDownloads>())
            .AddSingleton<HostThrottle>()
            .AddSingleton<IArchiveDownloadJobs, ArchiveDownloadJobs>()
            .AddSingleton<IArchiveAcquirer>(provider => new ArchiveAcquirer(
                provider.GetRequiredService<ILibraryService>(),
                provider.GetRequiredService<ILibraryArchiveLocator>(),
                provider.GetRequiredService<IConnection>(),
                provider.GetRequiredService<IArchiveDownloadJobs>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                provider.GetRequiredService<IOSInterop>(),
                provider.GetRequiredService<ExpectedNexusDownloads>(),
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                provider.GetRequiredService<ISettingsManager>(),
                provider.GetRequiredService<ILogger<ArchiveAcquirer>>(),
                provider.GetService<IHostApplicationLifetime>()))
            .AddSingleton<ModlistDownloads>()
            .AddSingleton<ArchiveInbox>()
            .AddSingleton<DownloadsPickup>()
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

    /// <summary>
    /// Continues, once the window is up, the modlist download the last session left unfinished. Only the window
    /// process adds it: a game launch or a one-shot verb in a headless main must not start hours of downloads that
    /// end with the process.
    /// </summary>
    public static IServiceCollection AddModlistDownloadResumer(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ModlistDownloadResumer>());
        return services;
    }

    public static IServiceCollection AddWabbajackSettings(this IServiceCollection services)
    {
        return services.AddSettings<WabbajackSettings>().AddDownloadSettings();
    }
}

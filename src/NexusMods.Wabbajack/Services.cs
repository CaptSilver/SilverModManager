using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Sdk.Settings;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Cli;
using NexusMods.Wabbajack.Diagnostics;
using NexusMods.Wabbajack.Install;
using NexusMods.Wabbajack.Library;
using NexusMods.Wabbajack.Models;

namespace NexusMods.Wabbajack;

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
            .AddSingleton<ExpectedNexusDownloads>()
            .AddSingleton<IExpectedNexusDownloads>(provider => provider.GetRequiredService<ExpectedNexusDownloads>())
            .AddSingleton<IArchiveDownloadJobs, ArchiveDownloadJobs>()
            .AddSingleton<IArchiveAcquirer, ArchiveAcquirer>()
            .AddSingleton<ArchiveInbox>()
            .AddSingleton<ILibraryFileRecogniser, WabbajackModlistRecogniser>();
    }

    public static IServiceCollection AddWabbajackSettings(this IServiceCollection services)
    {
        return services.AddSettings<WabbajackSettings>();
    }
}

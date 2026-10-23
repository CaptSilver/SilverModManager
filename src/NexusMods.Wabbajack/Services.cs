using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Sdk.Settings;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Install;
using NexusMods.Wabbajack.Library;
using NexusMods.Wabbajack.Models;

namespace NexusMods.Wabbajack;

[PublicAPI]
public static class Services
{
    public static IServiceCollection AddWabbajack(this IServiceCollection services)
    {
        return services
            .AddWabbajackModlistLibraryFileModel()
            .AddWabbajackModlistLoadoutGroupModel()
            .AddWabbajackSkippedFileModel()
            .AddWabbajackSettings()
            .AddSingleton<WabbajackPaths>()
            .AddSingleton<ILibraryArchiveLocator, LibraryArchiveLocator>()
            .AddSingleton<ArchiveInventory>()
            .AddSingleton<IDirectiveTransforms, UnavailableDirectiveTransforms>()
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

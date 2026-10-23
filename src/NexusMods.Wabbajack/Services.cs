using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Sdk.Settings;
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
            .AddWabbajackSettings()
            .AddSingleton<WabbajackPaths>()
            .AddSingleton<ILibraryArchiveLocator, LibraryArchiveLocator>()
            .AddSingleton<ILibraryFileRecogniser, WabbajackModlistRecogniser>();
    }

    public static IServiceCollection AddWabbajackSettings(this IServiceCollection services)
    {
        return services.AddSettings<WabbajackSettings>();
    }
}

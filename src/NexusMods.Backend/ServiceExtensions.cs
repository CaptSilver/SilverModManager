using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Backend.FileExtractor;
using NexusMods.Backend.FileExtractor.Extractors;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Backend.Games;
using NexusMods.Backend.Games.Locators;
using NexusMods.Backend.Games.UserGames;
using NexusMods.Backend.Jobs;
using NexusMods.Backend.OS;
using NexusMods.Backend.Process;
using NexusMods.Backend.RuntimeDependency;
using NexusMods.FileExtractor;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileExtractor;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Settings;
using NexusMods.UI.Sdk.Icons;
using NexusMods.UI.Sdk.Settings;

namespace NexusMods.Backend;

public static class ServiceExtensions
{
    public static IServiceCollection AddGameLocators(this IServiceCollection serviceCollection)
    {
        // Steam runs the games through Proton, so there is no Windows registry to hand to the locator.
        serviceCollection.AddSingleton<IGameLocator>(serviceProvider => new SteamLocator(serviceProvider.GetServices<IGameData>(), serviceProvider.GetRequiredService<ILoggerFactory>(), serviceProvider.GetRequiredService<IFileSystem>(), registry: null));
        serviceCollection.AddSingleton<IGameLocator, HeroicGOGLocator>();
        serviceCollection.AddSingleton<IGameLocator, HeroicEpicLocator>();
        serviceCollection.AddSingleton<IGameLocator, HeroicAmazonLocator>();

        // GOG and Epic installs made through Wine live in prefixes, each with its own registry and file system overlay.
        serviceCollection.AddSingleton<IGameLocator>(serviceProvider =>
        {
            var locatorFactories = new WinePrefixWrappingLocator.LocatorFactory[]
            {
                (provider, loggerFactory, fileSystem, registry) => new GOGLocator(provider.GetServices<IGameData>(), loggerFactory, fileSystem, registry),
                (provider, loggerFactory, fileSystem, registry) => new EGSLocator(provider.GetServices<IGameData>(), loggerFactory, fileSystem, registry),
            };

            return new WinePrefixWrappingLocator(serviceProvider, locatorFactories);
        });

        return serviceCollection;
    }

    public static IServiceCollection AddGameServices(this IServiceCollection serviceCollection)
    {
        return serviceCollection
            .AddSingleton<IGameLocationsService, GameLocationsService>()
            .AddSingleton<IGameRegistry, GameRegistry>()
            .AddSingleton<IGameArtSource, SteamLibraryCacheArtSource>()
            .AddGameInstallMetadataModel()
            .AddSingleton<IGameLocator, UserInstallLocator>();
    }

    /// <summary>
    /// Keeps the user's recorded game installs in <c>installs.yaml</c>. Only the app host registers this;
    /// test hosts register an in-memory store so they don't share the file.
    /// </summary>
    public static IServiceCollection AddYamlUserGameInstallStore(this IServiceCollection serviceCollection)
        => serviceCollection.AddSingleton<IUserGameInstallStore, YamlUserGameInstallStore>();

    public static IServiceCollection AddOSInterop(this IServiceCollection serviceCollection, IOSInformation? os = null)
    {
        os ??= OSInformation.Shared;

        return serviceCollection
            .AddSingleton(os)
            .AddSingleton<IProcessRunner, ProcessRunner>()
            .AddSingleton<IOSInterop, LinuxInterop>();
    }

    public static IServiceCollection AddRuntimeDependencies(this IServiceCollection serviceCollection)
    {
        return serviceCollection
            .AddHostedService<RuntimeDependencyChecker>()
            .AddAllSingleton<IRuntimeDependency, XdgSettingsDependency>()
            .AddAllSingleton<IRuntimeDependency, UpdateDesktopDatabaseDependency>();
    }

    /// <summary>
    /// Drops the host-start probe of the runtime dependencies. Every probe starts an external tool
    /// (<c>flatpak run ... --version</c>, <c>xdg-settings --version</c>), which test hosts must not do on the
    /// developer's machine. The dependencies themselves stay registered.
    /// </summary>
    public static IServiceCollection RemoveRuntimeDependencyChecker(this IServiceCollection serviceCollection)
    {
        var checkers = serviceCollection.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RuntimeDependencyChecker)).ToArray();
        foreach (var descriptor in checkers) serviceCollection.Remove(descriptor);
        return serviceCollection;
    }

    public static IServiceCollection AddSettingsManager(this IServiceCollection serviceCollection)
    {
        return serviceCollection
            .AddSingleton<ISettingsManager, SettingsManager>()
            .AddStorageBackend<JsonStorageBackend>()
            .AddStorageBackend<MnemonicDBStorageBackend>(isDefault: true)
            .AddSingleton(new SectionDescriptor(
                Id: Sections.General,
                Name: "General",
                IconFunc: () => IconValues.Desktop,
                Priority: ushort.MaxValue
            ))
            .AddSingleton(new SectionDescriptor(
                Id: Sections.Privacy,
                Name: "Privacy",
                IconFunc: () => IconValues.ShieldHalfFull
            ))
            .AddSingleton(new SectionDescriptor(
                Id: Sections.GameSpecific,
                Name: "Game specific",
                IconFunc: () => IconValues.Game,
                Priority: ushort.MinValue + 3
            ))
            .AddSingleton(new SectionDescriptor(
                Id: Sections.Advanced,
                Name: "Advanced",
                IconFunc: () => IconValues.School,
                Priority: ushort.MinValue + 2
            ))
            .AddSingleton(new SectionDescriptor(
                Id: Sections.DeveloperTools,
                Name: "Developer tools",
                IconFunc: () => IconValues.Code,
                Priority: ushort.MinValue + 1
            ))
            .AddSingleton(new SectionDescriptor(
                Id: Sections.Experimental,
                Name: "Experimental - Not currently supported",
                IconFunc: () => IconValues.WarningAmber,
                Priority: ushort.MinValue,
                Hidden: !ApplicationConstants.IsDebug
            ))
            .AddSettingModel();
    }
    
    /// <summary>
    /// Adds file extraction related services to the provided DI container.
    /// </summary>
    public static IServiceCollection AddFileExtractors(this IServiceCollection coll)
    {
        coll.AddSettings<FileExtractorSettings>();
        coll.AddFileExtractorVerbs();
        coll.AddSingleton<IFileExtractor, FileExtractor.FileExtractor>();
        coll.AddSingleton<IExtractor, SevenZipExtractor>();
        coll.AddSingleton<IExtractor, ManagedZipExtractor>();
        coll.TryAddSingleton<TemporaryFileManager, TemporaryFileManagerEx>();
        return coll;
    }
    
    public static IServiceCollection AddJobMonitor(this IServiceCollection serviceCollection)
    {
        return serviceCollection
            .AddSingleton<IJobMonitor, JobMonitor>();
    }
}

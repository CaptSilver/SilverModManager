using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Secrets;
using SilverModManager.Abstractions.Serialization;
using SilverModManager.Backend;
using SilverModManager.CrossPlatform;
using SilverModManager.DataModel;
using SilverModManager.FileExtractor;
using SilverModManager.Games.FileHashes;
using SilverModManager.Library;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Networking.HttpDownloader.Tests;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;
using Xunit.DependencyInjection.Logging;

namespace SilverModManager.Networking.NexusWebApi.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services
            .AddGameServices()
            .AddSerializationAbstractions()
            .AddFileSystem()
            .AddSettingsManager()
            .AddHttpDownloader()
            .AddSingleton<TemporaryFileManager>()
            .AddSingleton<LocalHttpServer>()
            .AddSecrets(new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive, Backend = SecretBackendChoice.Memory })
            .AddNexusWebApi()
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddSettings<LoggingSettings>()
            .AddLoadoutAbstractions()
            .AddJobMonitor()
            .AddLibrary()
            .AddLibraryModels()
            .AddFileExtractors()
            .AddFileHashes()
            .AddDataModel() // this is required because we're also using NMA integration
            .OverrideSettingsForTests<FileHashesServiceSettings>(settings => settings with { UseInMemoryLocalBaseline = true, })
            .OverrideSettingsForTests<DataModelSettings>(settings => settings with
            {
                UseInMemoryDataModel = true,
            })
            .AddLogging(builder => builder.AddXunitOutput()
                .SetMinimumLevel(LogLevel.Debug))
            .Validate();
    }
}


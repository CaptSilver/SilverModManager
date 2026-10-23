using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Abstractions.Serialization;
using SilverModManager.Backend;
using SilverModManager.CrossPlatform;
using SilverModManager.DataModel;
using SilverModManager.FileExtractor;
using SilverModManager.Games.FileHashes;
using SilverModManager.Library;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Networking.HttpDownloader.Tests;
using SilverModManager.Networking.NexusWebApi;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;
using SilverModManager.Secrets;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.Wabbajack.Cli;
using Xunit.DependencyInjection.Logging;

namespace SilverModManager.CLI.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        const KnownPath baseKnownPath = KnownPath.EntryDirectory;
        var baseDirectory = $"SilverModManager.UI.Tests.Tests-{Guid.NewGuid()}";

        services
                .AddSingleton<CommandLineConfigurator>()
                .AddFileSystem()
                .AddSettingsManager()
                .AddDataModel()
                .AddLibrary()
                .AddLibraryModels()
                .AddJobMonitor()
                .OverrideSettingsForTests<FileHashesServiceSettings>(settings => settings with { UseInMemoryLocalBaseline = true, })
                .OverrideSettingsForTests<DataModelSettings>(settings => settings with
                {
                    UseInMemoryDataModel = true,
                    MnemonicDBPath = new ConfigurablePath(baseKnownPath, $"{baseDirectory}/MnemonicDB.rocksdb"),
                    ArchiveLocations = [
                        new ConfigurablePath(baseKnownPath, $"{baseDirectory}/Archives"),
                    ],
                })
                .AddFileExtractors()
                .AddFileHashes()
                .AddCLI()
                .AddWabbajackVerbs()
                .AddHttpDownloader()
                .AddSecrets(new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive, Backend = SecretBackendChoice.Memory })
                .AddNexusWebApi()
                .AddLoadoutAbstractions()
                .AddSerializationAbstractions()
                .AddGames()
                .AddGameServices()
                .AddInMemoryUserGameInstallStore()
                .AddOSInterop()
                .AddRuntimeDependencies()
                .RemoveRuntimeDependencyChecker()
                .AddSettings<LoggingSettings>()
                .AddLogging(builder => builder.AddXunitOutput().SetMinimumLevel(LogLevel.Trace))
                .AddSingleton<LocalHttpServer>()
                .AddLogging(builder => builder.AddXUnit())
                .ReplaceDesktopNotifier()
                .Validate();
    }
}


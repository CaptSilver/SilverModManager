using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Sdk.Settings;
using SilverModManager.Backend;
using SilverModManager.DataModel;
using SilverModManager.Networking.HttpDownloader;
using NexusMods.Paths;

namespace SilverModManager.Library.Tests.DownloadsService;

public static class DIHelpers
{
    public static void ConfigureServices(IServiceCollection services)
    {
        // Setup base path for test files
        const KnownPath baseKnownPath = KnownPath.EntryDirectory;
        var baseDirectory = $"SilverModManager.Library.Tests-{Guid.NewGuid()}";
        var prefix = FileSystem.Shared.GetKnownPath(baseKnownPath).Combine(baseDirectory);
        
        services
            // Add logging
            .AddLogging(builder => builder.AddXUnit())
            
            // Add FileSystem support
            .AddFileSystem()
            .AddSingleton<TemporaryFileManager>(_ => new TemporaryFileManager(FileSystem.Shared, prefix))
            
            // Add JobMonitor - this provides real IJobMonitor implementation
            .AddJobMonitor()
            
            // Add HttpClient for download jobs
            .AddHttpDownloader()
            
            // Add DataModel - provides complete MnemonicDB setup including in-memory settings
            .AddDataModel()
            .AddNexusModsLibraryModels()
            .AddSettingsManager()
            .OverrideSettingsForTests<DataModelSettings>(settings => settings with
            {
                UseInMemoryDataModel = true,
            })

            // Add DownloadsService itself
            .AddSingleton<Library.DownloadsService>();
    }
}

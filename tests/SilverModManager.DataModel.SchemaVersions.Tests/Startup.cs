using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.App;
using SilverModManager.Backend;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.Games.FileHashes;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

public class Startup
{
    /// <summary>
    /// Why are Cyberpunk tests in a generic DataModel project, well it's, so we can test something that's close to real-world data. 
    /// </summary>
    /// <param name="container"></param>
    public void ConfigureServices(IServiceCollection container)
    {
        // The GUI graph: the schema under test is everything a real app start registers, the UI's models included.
        var mode = new StartupMode { RunAsMain = true, ShowUI = true, SecretBackend = SecretBackendChoice.Memory };
        
        var baseDirectory = $"SilverModManager.UI.Tests.Tests-{Guid.NewGuid()}";
        
        var path = FileSystem.Shared.FromUnsanitizedFullPath(TestScratch.Combine("temp", Guid.NewGuid().ToString()));
        path.CreateDirectory();

        container
            .AddApp(addStandardGameLocators: false, startupMode: mode)
            .OverrideSettingsForTests<FileHashesServiceSettings>(settings => settings with { UseInMemoryLocalBaseline = true, })
            .OverrideSettingsForTests<DataModelSettings>(settings => settings with
                {
                    UseInMemoryDataModel = true,
                    MnemonicDBPath = new ConfigurablePath(null, TestScratch.Combine($"{baseDirectory}/MnemonicDB.rocksdb")),
                    ArchiveLocations =
                    [
                        new ConfigurablePath(null, TestScratch.Combine($"{baseDirectory}/Archives")),
                    ],
                }
            )
            .RemoveLauncherShimRefresher()
            .RemoveRuntimeDependencyChecker();
    }
}


using Microsoft.Extensions.DependencyInjection;
using NexusMods.App;
using NexusMods.Backend;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.Games.FileHashes;

namespace NexusMods.DataModel.SchemaVersions.Tests;

public class Startup
{
    /// <summary>
    /// Why are Cyberpunk tests in a generic DataModel project, well it's, so we can test something that's close to real-world data. 
    /// </summary>
    /// <param name="container"></param>
    public void ConfigureServices(IServiceCollection container)
    {
        // The GUI graph: the schema under test is everything a real app start registers, the UI's models included.
        var mode = new StartupMode { RunAsMain = true, ShowUI = true };
        
        const KnownPath baseKnownPath = KnownPath.EntryDirectory;
        var baseDirectory = $"NexusMods.UI.Tests.Tests-{Guid.NewGuid()}";
        
        var path = FileSystem.Shared.GetKnownPath(KnownPath.EntryDirectory).Combine("temp").Combine(Guid.NewGuid().ToString());
        path.CreateDirectory();

        container
            .AddApp(startupMode: mode)
            .OverrideSettingsForTests<FileHashesServiceSettings>(settings => settings with { UseInMemoryLocalBaseline = true, })
            .OverrideSettingsForTests<DataModelSettings>(settings => settings with
                {
                    UseInMemoryDataModel = true,
                    MnemonicDBPath = new ConfigurablePath(baseKnownPath, $"{baseDirectory}/MnemonicDB.rocksdb"),
                    ArchiveLocations =
                    [
                        new ConfigurablePath(baseKnownPath, $"{baseDirectory}/Archives"),
                    ],
                }
            )
            .RemoveLauncherShimRefresher()
            .RemoveRuntimeDependencyChecker();
    }
}


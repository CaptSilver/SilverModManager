using Microsoft.Extensions.DependencyInjection;
using NexusMods.App;
using NexusMods.DataModel;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;

namespace NexusMods.DataModel.SchemaVersions.Tests;

/// <summary>
/// A throwaway folder next to the test binaries holding a real on-disk database, for tests
/// that need what an actual app start writes rather than the in-memory model.
/// </summary>
internal sealed class OnDiskDataFolder : IDisposable
{
    private readonly string _name = $"FreshDataDirectory-{Guid.NewGuid()}";

    public ServiceProvider CreateProvider()
    {
        return new ServiceCollection()
            .AddLogging()
            .AddApp(startupMode: new StartupMode { RunAsMain = true })
            .OverrideSettingsForTests<DataModelSettings>(settings => settings with
            {
                UseInMemoryDataModel = false,
                MnemonicDBPath = new ConfigurablePath(KnownPath.EntryDirectory, $"{_name}/MnemonicDB.rocksdb"),
                ArchiveLocations = [new ConfigurablePath(KnownPath.EntryDirectory, $"{_name}/Archives")],
            })
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        var root = FileSystem.Shared.GetKnownPath(KnownPath.EntryDirectory).Combine(_name);
        if (root.DirectoryExists()) root.DeleteDirectory(recursive: true);
    }
}

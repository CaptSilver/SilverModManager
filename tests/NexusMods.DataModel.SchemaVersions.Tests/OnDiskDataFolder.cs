using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Secrets;
using NexusMods.App;
using NexusMods.Backend;
using NexusMods.DataModel;
using NexusMods.Games.FileHashes;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.DataModel.SchemaVersions.Tests;

/// <summary>
/// A throwaway folder next to the test binaries holding a real on-disk database, for tests
/// that need what an actual app start writes rather than the in-memory model.
/// </summary>
internal sealed class OnDiskDataFolder : IDisposable
{
    private readonly string _name = $"FreshDataDirectory-{Guid.NewGuid()}";

    /// <summary>
    /// Where the provider opens the database. Fill it before calling <see cref="CreateProvider"/> to start from an existing one.
    /// </summary>
    public AbsolutePath DatabaseFolder => DatabasePath.ToPath(FileSystem.Shared);

    private ConfigurablePath DatabasePath => new(KnownPath.EntryDirectory, $"{_name}/MnemonicDB.rocksdb");

    public static StartupMode Gui => new() { RunAsMain = true, ShowUI = true, SecretBackend = SecretBackendChoice.Memory };
    public static StartupMode Headless => new() { RunAsMain = true, ShowUI = false, SecretBackend = SecretBackendChoice.Memory };

    public ServiceProvider CreateProvider(StartupMode? mode = null)
    {
        mode ??= Gui;
        var services = new ServiceCollection()
            .AddLogging()
            .AddApp(addStandardGameLocators: false, startupMode: mode)
            .OverrideSettingsForTests<FileHashesServiceSettings>(settings => settings with
            {
                LocalBaselineLocation = new ConfigurablePath(KnownPath.EntryDirectory, $"{_name}/LocalVanillaBaseline"),
            })
            .OverrideSettingsForTests<DataModelSettings>(settings => settings with
            {
                UseInMemoryDataModel = false,
                MnemonicDBPath = DatabasePath,
                ArchiveLocations = [new ConfigurablePath(KnownPath.EntryDirectory, $"{_name}/Archives")],
            });

        services.RemoveRuntimeDependencyChecker();

        // Only the GUI graph registers the refresher, and the helper throws when it is missing.
        if (mode.ShowUI) services.RemoveLauncherShimRefresher();
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        var root = FileSystem.Shared.GetKnownPath(KnownPath.EntryDirectory).Combine(_name);
        if (root.DirectoryExists()) root.DeleteDirectory(recursive: true);
    }
}

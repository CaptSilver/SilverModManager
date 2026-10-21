using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Storage;
using NexusMods.MnemonicDB.Storage.RocksDbBackend;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Settings;
using NSubstitute;
using Connection = NexusMods.MnemonicDB.Connection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusMods.Games.FileHashes.Tests;

/// <summary>
/// Builds <see cref="FileHashesService"/> instances over a <see cref="RealQueryEngineHost"/> and a temp
/// directory, and writes the databases they open: the central database is a directory on disk, the
/// way a downloaded one looks to the service.
/// </summary>
internal sealed class ServiceHarness : IDisposable
{
    private static readonly JsonSerializerOptions AppJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(), new HashJsonConverter() },
    };

    private readonly List<IDisposable> _services = [];
    private readonly List<string> _lockedDirectories = [];

    public ServiceHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "file-hashes-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Host = new RealQueryEngineHost();
    }

    public string Root { get; }

    public RealQueryEngineHost Host { get; }

    public string NewDatabaseLocation()
    {
        var path = Path.Combine(Root, "databases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Makes <paramref name="directory"/> impossible to write into until the harness is disposed.</summary>
    public void MakeReadOnly(string directory)
    {
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        _lockedDirectories.Add(directory);
    }

    public FileHashesService CreateService(string databaseLocation, ILogger<FileHashesService>? logger = null)
    {
        var settings = new FileHashesServiceSettings
        {
            HashDatabaseLocation = new ConfigurablePath(KnownPath.TempDirectory, Path.GetRelativePath(Path.GetTempPath(), databaseLocation)),
            UseInMemoryLocalBaseline = true,
            SkipUpdateCheck = true,
        };
        var settingsManager = Substitute.For<ISettingsManager>();
        settingsManager.Get<FileHashesServiceSettings>().Returns(settings);

        var service = new FileHashesService(
            logger ?? NullLogger<FileHashesService>.Instance,
            settingsManager,
            FileSystem.Shared,
            new HttpClient(),
            AppJsonOptions,
            Host.Provider);
        _services.Add(service);
        return service;
    }

    /// <summary>Disposes a service before the harness is, so the harness does not dispose it a second time.</summary>
    public void DisposeService(FileHashesService service)
    {
        _services.Remove(service);
        service.Dispose();
    }

    /// <summary>
    /// Writes a database to <c>{guid}_{timestamp}</c> below the location, fills it and closes it again.
    /// </summary>
    public async Task CreateDatabaseOnDisk(string location, long timestamp, Func<IConnection, Task> fill)
    {
        var path = Path.Combine(location, $"{Guid.NewGuid()}_{timestamp}");
        using var backend = new Backend();
        using var store = new DatomStore(NullLogger<DatomStore>.Instance, new DatomStoreSettings { Path = FileSystem.Shared.FromUnsanitizedFullPath(path) }, backend);
        using var connection = new Connection(NullLogger<Connection>.Instance, store, Host.Provider, []);
        await fill(connection);
    }

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        Host.Dispose();
        foreach (var directory in _lockedDirectories)
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(Root, recursive: true);
    }
}

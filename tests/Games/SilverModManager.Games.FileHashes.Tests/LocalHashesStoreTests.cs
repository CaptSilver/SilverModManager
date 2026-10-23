using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using NexusMods.HyperDuck;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.RocksDbBackend;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Settings;
using NSubstitute;
using Connection = SilverModManager.MnemonicDB.Connection;

namespace SilverModManager.Games.FileHashes.Tests;

public sealed class LocalHashesStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions AppJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(), new HashJsonConverter(), new RelativePathJsonConverter() },
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "local-hashes-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RealQueryEngineHost _host = new();
    private readonly List<IDisposable> _services = [];
    private readonly ITestOutputHelper _output;

    public LocalHashesStoreTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        _host.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Current_BeforeAnyCentralDatabaseIsOpened_ReturnsTheLocalStore()
    {
        var service = CreateService();

        service.Current.Connection.Should().BeSameAs(service.Local.Connection);
    }

    [Fact]
    public async Task Local_ReadsRowsCommittedAfterTheFirstRead()
    {
        var service = CreateService();
        var before = service.Local;
        HashRelation.All(before).Should().BeEmpty();

        await RealQueryEngineHost.AddHashRow(service.Local.Connection, "committed later");

        HashRelation.All(service.Local).Should().ContainSingle();
    }

    [Fact]
    public void Databases_ListsTheLocalStoreFirst()
    {
        var service = CreateService();

        service.Databases.Should().ContainSingle().Which.Connection.Should().BeSameAs(service.Local.Connection);
    }

    [Fact]
    public async Task Databases_WithACentralDatabaseConnected_ListsLocalThenCentral()
    {
        var location = NewDatabaseLocation();
        await CreateDatabaseOnDisk(location, 1000, "central row");
        var service = CreateService(location);
        await service.CheckForUpdate();

        service.Databases.Should().HaveCount(2);
        service.Databases[0].Connection.Should().BeSameAs(service.Local.Connection);
        service.Databases[1].Connection.Should().NotBeSameAs(service.Local.Connection);
        service.Current.Connection.Should().BeSameAs(service.Databases[1].Connection);
    }

    [Fact]
    public void SettingsFileWithoutTheLocalBaselineKey_GetsTheDefaultLocation()
    {
        // Keys as an install that predates the local store has them on disk.
        const string json = """
            {
              "HashDatabaseLocation": { "BaseDirectory": "XDG_DATA_HOME", "File": "SilverModManager/FileHashesDatabase" },
              "HashDatabaseUpdateInterval": "00:30:00",
              "SkipUpdateCheck": false,
              "GithubManifestUrl": "https://github.com/CaptSilver/game-hashes/releases/latest/download/manifest.json",
              "GameHashesDbUrl": "https://github.com/CaptSilver/game-hashes/releases/latest/download/game_hashes_db.zip",
              "ReleaseFilePath": "game-hashes-release.json"
            }
            """;

        var settings = JsonSerializer.Deserialize<FileHashesServiceSettings>(json, AppJsonOptions)!;

        settings.LocalBaselineLocation.Should().Be(new ConfigurablePath(KnownPath.XDG_DATA_HOME, $"{ApplicationConstants.DataDirectoryName}/LocalVanillaBaseline"));
        settings.UseInMemoryLocalBaseline.Should().BeFalse();
    }

    [Fact]
    public async Task ReplacingTheCentralDatabase_LeavesOneConnectionNamedHashes()
    {
        var location = NewDatabaseLocation();
        await CreateDatabaseOnDisk(location, 1000, "older row");
        var service = CreateService(location);
        await service.CheckForUpdate();
        var newerHash = await CreateDatabaseOnDisk(location, 2000, "newer row");

        await service.CheckForUpdate();

        var names = service.Local.Connection.Query<string>($"SELECT Name FROM mnemonicdb_active_connections()");
        names.Where(n => n.Equals("hashes", StringComparison.OrdinalIgnoreCase)).Should().ContainSingle();
        service.Local.Connection.Query<Hash>($"SELECT XxHash3 FROM MDB_HASHRELATION(DbName=>'hashes')")
            .Should().ContainSingle().Which.Should().Be(newerHash);
    }

    [Fact]
    public async Task ReturningToAnEarlierDatabase_RegistersItUnderHashesAgain()
    {
        var location = NewDatabaseLocation();
        var olderHash = await CreateDatabaseOnDisk(location, 1000, "older row");
        var service = CreateService(location);
        await service.CheckForUpdate();
        var olderPath = Directory.GetDirectories(location).Single();
        await CreateDatabaseOnDisk(location, 2000, "newer row");
        await service.CheckForUpdate();

        // The newest database vanishing makes the older one the newest on disk again.
        Directory.Delete(Directory.GetDirectories(location).Single(d => d != olderPath), recursive: true);
        await service.CheckForUpdate();

        service.Local.Connection.Query<Hash>($"SELECT XxHash3 FROM MDB_HASHRELATION(DbName=>'hashes')")
            .Should().ContainSingle().Which.Should().Be(olderHash);
    }

    [Fact]
    public async Task ADbReturnedBeforeTheSwap_StaysReadable()
    {
        var location = NewDatabaseLocation();
        var olderHash = await CreateDatabaseOnDisk(location, 1000, "older row");
        var service = CreateService(location);
        await service.CheckForUpdate();
        var held = service.Current;
        await CreateDatabaseOnDisk(location, 2000, "newer row");

        await service.CheckForUpdate();

        HashRelation.All(held).Select(r => r.XxHash3).Should().ContainSingle().Which.Should().Be(olderHash);
    }

    [Fact]
    public void ActiveConnections_ListBothNamedStores()
    {
        var hashes = _host.OpenInMemory("hashes");
        _host.OpenInMemory("local_hashes");

        var names = hashes.Query<string>($"SELECT Name FROM mnemonicdb_active_connections()");

        names.Should().Contain(["hashes", "local_hashes"]);
        foreach (var row in hashes.Query<(string Name, ulong LastTxId, string Path)>($"SELECT Name, LastTxId, Path FROM mnemonicdb_active_connections()"))
            _output.WriteLine($"{row.Name}\t{row.LastTxId}\t{row.Path}");
    }

    [Fact]
    public async Task NamedStore_IsReadableByNameAndLive()
    {
        var hashes = _host.OpenInMemory("hashes");
        var local = _host.OpenInMemory("local_hashes");
        var centralHash = await RealQueryEngineHost.AddHashRow(hashes, "central");
        var firstLocal = await RealQueryEngineHost.AddHashRow(local, "local one");

        hashes.Query<Hash>($"SELECT XxHash3 FROM MDB_HASHRELATION(DbName=>'local_hashes')")
            .Should().ContainSingle().Which.Should().Be(firstLocal);

        var secondLocal = await RealQueryEngineHost.AddHashRow(local, "local two");

        hashes.Query<Hash>($"SELECT XxHash3 FROM MDB_HASHRELATION(DbName=>'local_hashes')")
            .Should().BeEquivalentTo([firstLocal, secondLocal]);
        hashes.Query<Hash>($"SELECT XxHash3 FROM MDB_HASHRELATION(DbName=>'hashes')")
            .Should().ContainSingle().Which.Should().Be(centralHash);
    }

    [Fact]
    public void SecondConnectionUnderTheSameName_IsNotReplaced()
    {
        var first = _host.OpenInMemory("hashes");
        _host.OpenInMemory("hashes");

        var names = first.Query<string>($"SELECT Name FROM mnemonicdb_active_connections()");

        names.Count(n => n.Equals("hashes", StringComparison.OrdinalIgnoreCase)).Should().Be(2);
    }

    [Fact]
    public void MissingName_FailsAtBind()
    {
        var hashes = _host.OpenInMemory("hashes");

        var act = () => hashes.Query<Hash>($"SELECT XxHash3 FROM MDB_HASHRELATION(DbName=>'no_such_store')").ToList();

        act.Should().Throw<Exception>().Where(e => e.ToString().Contains("No database named no_such_store"));
    }

    private string NewDatabaseLocation()
    {
        var path = Path.Combine(_root, "databases");
        Directory.CreateDirectory(path);
        return path;
    }

    private FileHashesService CreateService(string? databaseLocation = null)
    {
        var settings = new FileHashesServiceSettings
        {
            HashDatabaseLocation = new ConfigurablePath(KnownPath.TempDirectory, Path.GetRelativePath(Path.GetTempPath(), databaseLocation ?? NewDatabaseLocation())),
            LocalBaselineLocation = new ConfigurablePath(KnownPath.TempDirectory, Path.GetRelativePath(Path.GetTempPath(), Path.Combine(_root, "local-baseline"))),
            SkipUpdateCheck = true,
        };
        var settingsManager = Substitute.For<ISettingsManager>();
        settingsManager.Get<FileHashesServiceSettings>().Returns(settings);

        var service = new FileHashesService(
            NullLogger<FileHashesService>.Instance,
            settingsManager,
            FileSystem.Shared,
            new HttpClient(),
            AppJsonOptions,
            _host.Provider);
        _services.Add(service);
        return service;
    }

    /// <summary>
    /// Writes a database holding one hash row to <c>{guid}_{timestamp}</c> below the location and closes it
    /// again, which is how a downloaded database looks to the service.
    /// </summary>
    private async Task<Hash> CreateDatabaseOnDisk(string location, long timestamp, string content)
    {
        var path = Path.Combine(location, $"{Guid.NewGuid()}_{timestamp}");
        using var backend = new Backend();
        using var store = new DatomStore(NullLogger<DatomStore>.Instance, new DatomStoreSettings { Path = FileSystem.Shared.FromUnsanitizedFullPath(path) }, backend);
        using var connection = new Connection(NullLogger<Connection>.Instance, store, _host.Provider, []);
        return await RealQueryEngineHost.AddHashRow(connection, content);
    }
}

// The app's converter lives in the data model project, which these tests do not reference.
file sealed class RelativePathJsonConverter : JsonConverter<RelativePath>
{
    public override RelativePath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => RelativePath.FromUnsanitizedInput(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, RelativePath value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

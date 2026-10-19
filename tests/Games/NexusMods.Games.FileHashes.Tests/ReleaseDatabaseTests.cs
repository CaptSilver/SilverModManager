using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Games.FileHashes.DTOs;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Settings;
using NSubstitute;

namespace NexusMods.Games.FileHashes.Tests;

public sealed class ReleaseDatabaseTests : IDisposable
{
    private static readonly DateTimeOffset ReleaseTime = DateTimeOffset.FromUnixTimeSeconds(1_759_261_221);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "file-hashes-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _databaseLocation;
    private readonly string _outside;
    private readonly CapturingLogger _logger = new();
    private readonly FileHashesServiceSettings _settings;
    private byte[] _downloadBody = [];

    public ReleaseDatabaseTests()
    {
        // The database location sits one level below `_outside` so a `../` entry has
        // somewhere to land that is neither the extraction directory nor the location itself.
        _outside = Path.Combine(_root, "outside");
        _databaseLocation = Path.Combine(_outside, "databases");
        Directory.CreateDirectory(_databaseLocation);

        _settings = new FileHashesServiceSettings
        {
            HashDatabaseLocation = new ConfigurablePath(KnownPath.TempDirectory, Path.GetRelativePath(Path.GetTempPath(), _databaseLocation)),
        };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ParentDirectoryEntry_IsNotWrittenOutsideExtractionDirectory()
    {
        var zip = BuildZip(("../escaped.txt", "payload"), ("db/data.bin", "ok"));

        var result = await AddRelease(zip, await ManifestFor(zip));

        // The extraction directory is `<location>/<guid>_<ts>_tmp`, so `../escaped.txt` lands in the location itself.
        File.Exists(Path.Combine(_databaseLocation, "escaped.txt")).Should().BeFalse("the entry would escape the extraction directory");
        AssertNothingOutsideDatabaseLocation();
        result.HasValue.Should().BeFalse();
        _logger.Errors.Should().ContainSingle();
        Directory.GetFileSystemEntries(_databaseLocation).Should().BeEmpty("a rejected archive leaves no temp files or half-extracted directories");
    }

    [Fact]
    public async Task DeeplyNestedParentDirectoryEntry_IsNotWrittenOutsideExtractionDirectory()
    {
        var zip = BuildZip(("a/../../../escaped.txt", "payload"));

        var result = await AddRelease(zip, await ManifestFor(zip));

        File.Exists(Path.Combine(_outside, "escaped.txt")).Should().BeFalse();
        File.Exists(Path.Combine(_databaseLocation, "escaped.txt")).Should().BeFalse();
        result.HasValue.Should().BeFalse();
        _logger.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task AbsolutePathEntry_IsNotWrittenOutsideExtractionDirectory()
    {
        var target = Path.Combine(_outside, "absolute-target.txt");
        var zip = BuildZip((target, "payload"));

        var result = await AddRelease(zip, await ManifestFor(zip));

        File.Exists(target).Should().BeFalse("an absolute entry name must not be honoured");
        result.HasValue.Should().BeFalse();
        _logger.Errors.Should().ContainSingle();
        Directory.GetFileSystemEntries(_databaseLocation).Should().BeEmpty();
    }

    [Fact]
    public async Task BackslashParentDirectoryEntry_IsNotWrittenOutsideExtractionDirectory()
    {
        // Entry names are normalised to forward slashes, so this is a `../` entry on Linux too.
        var zip = BuildZip(("..\\escaped.txt", "payload"));

        var result = await AddRelease(zip, await ManifestFor(zip));

        File.Exists(Path.Combine(_databaseLocation, "escaped.txt")).Should().BeFalse();
        result.HasValue.Should().BeFalse();
        _logger.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task SizeMismatch_RejectsDownloadAndKeepsExistingDatabase()
    {
        var existing = CreateExistingDatabase();
        var zip = BuildZip(("db/data.bin", "ok"));
        var manifest = await ManifestFor(zip);
        manifest.Assets[0].Size = Size.From(manifest.Assets[0].Size.Value + 1);

        var result = await AddRelease(zip, manifest);

        result.HasValue.Should().BeFalse();
        _logger.Errors.Should().ContainSingle();
        Directory.GetFileSystemEntries(_databaseLocation).Should().BeEquivalentTo([existing], "only the existing database remains");
        File.ReadAllText(Path.Combine(existing, "marker")).Should().Be("keep");
    }

    [Fact]
    public async Task HashMismatch_RejectsDownloadAndKeepsExistingDatabase()
    {
        var existing = CreateExistingDatabase();
        var zip = BuildZip(("db/data.bin", "aa"));
        var tampered = BuildZip(("db/data.bin", "bb"));
        tampered.Length.Should().Be(zip.Length, "only the hash may differ from the manifest");
        var manifest = await ManifestFor(zip);

        var result = await AddRelease(tampered, manifest);

        result.HasValue.Should().BeFalse();
        _logger.Errors.Should().ContainSingle();
        Directory.GetFileSystemEntries(_databaseLocation).Should().BeEquivalentTo([existing]);
    }

    [Fact]
    public async Task ManifestWithoutDatabaseAsset_RejectsDownload()
    {
        var zip = BuildZip(("db/data.bin", "ok"));
        var manifest = new Manifest { CreatedAt = ReleaseTime };

        var result = await AddRelease(zip, manifest);

        result.HasValue.Should().BeFalse();
        _logger.Errors.Should().ContainSingle();
        Directory.GetFileSystemEntries(_databaseLocation).Should().BeEmpty();
    }

    [Fact]
    public async Task MatchingDownload_IsExtractedIntoANewDatabaseDirectory()
    {
        var zip = BuildZip(("CURRENT", "manifest-1"), ("nested/000001.sst", "table"));

        var result = await AddRelease(zip, await ManifestFor(zip));

        result.HasValue.Should().BeTrue();
        _logger.Errors.Should().BeEmpty();
        var directory = result.Value.Path.ToString();
        Path.GetDirectoryName(directory).Should().Be(_databaseLocation);
        Path.GetFileName(directory).Should().EndWith($"_{ReleaseTime.ToUnixTimeSeconds()}");
        File.ReadAllText(Path.Combine(directory, "CURRENT")).Should().Be("manifest-1");
        File.ReadAllText(Path.Combine(directory, "nested", "000001.sst")).Should().Be("table");
        Directory.GetFileSystemEntries(_databaseLocation).Should().ContainSingle("the temp archive and extraction directory are cleaned up");
    }

    [Fact]
    public async Task RealRelease_HashAndSizeMatchManifest()
    {
        var (zipPath, manifestPath) = RealReleaseFiles();
        var asset = await ReadDatabaseAsset(manifestPath);

        var hash = await FileSystem.Shared.FromUnsanitizedFullPath(zipPath).XxHash3Async();

        hash.Should().Be(asset.Hash, "the manifest hash is the xxHash3 of the zip file, as written by the database builder");
        new FileInfo(zipPath).Length.Should().Be((long)asset.Size.Value);
    }

    [Fact]
    public async Task RealRelease_IsAccepted()
    {
        var (zipPath, manifestPath) = RealReleaseFiles();
        var manifest = new Manifest { CreatedAt = ReleaseTime, Assets = [await ReadDatabaseAsset(manifestPath)] };

        var result = await AddRelease(await File.ReadAllBytesAsync(zipPath), manifest);

        _logger.Errors.Should().BeEmpty();
        result.HasValue.Should().BeTrue();
        Directory.GetFiles(result.Value.Path.ToString(), "*", SearchOption.AllDirectories).Should().NotBeEmpty();
    }

    private static (string Zip, string Manifest) RealReleaseFiles()
    {
        var directory = Environment.GetEnvironmentVariable("GAME_HASHES_RELEASE_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(directory), "GAME_HASHES_RELEASE_DIR is not set");
        var zip = Path.Combine(directory!, "game_hashes_db.zip");
        var manifest = Path.Combine(directory!, "manifest.json");
        Assert.SkipUnless(File.Exists(zip) && File.Exists(manifest), "game_hashes_db.zip and manifest.json not found in GAME_HASHES_RELEASE_DIR");
        return (zip, manifest);
    }

    // Same converters the app registers for the manifest it downloads.
    private static readonly JsonSerializerOptions AppJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(), new HashJsonConverter() },
    };

    private static async Task<Asset> ReadDatabaseAsset(string manifestPath)
    {
        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, AppJsonOptions);
        return manifest!.Assets.Single(a => a.Type == AssetType.GameHashDb);
    }

    private void AssertNothingOutsideDatabaseLocation()
    {
        Directory.GetFileSystemEntries(_outside).Should().BeEquivalentTo([_databaseLocation], "nothing may be written next to the database location");
    }

    private string CreateExistingDatabase()
    {
        var path = Path.Combine(_databaseLocation, $"{Guid.NewGuid()}_{ReleaseTime.AddDays(-1).ToUnixTimeSeconds()}");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "marker"), "keep");
        return path;
    }

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return memory.ToArray();
    }

    /// <summary>
    /// A manifest describing <paramref name="zip"/>, hashed with the same call the database builder uses.
    /// </summary>
    private async Task<Manifest> ManifestFor(byte[] zip)
    {
        var path = Path.Combine(_root, "manifest-source-" + Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllBytesAsync(path, zip);
        var hash = await FileSystem.Shared.FromUnsanitizedFullPath(path).XxHash3Async();
        File.Delete(path);

        return new Manifest
        {
            CreatedAt = ReleaseTime,
            Assets = [new Asset { Type = AssetType.GameHashDb, Hash = hash, Size = Size.From((ulong)zip.Length) }],
        };
    }

    private async Task<Optional<FileHashesService.DatabaseInfo>> AddRelease(byte[] body, Manifest manifest)
    {
        _downloadBody = body;
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IQueryEngine>());
        var settingsManager = Substitute.For<ISettingsManager>();
        settingsManager.Get<FileHashesServiceSettings>().Returns(_settings);

        using var service = new FileHashesService(
            _logger,
            settingsManager,
            FileSystem.Shared,
            new HttpClient(new StubHandler(() => _downloadBody)),
            AppJsonOptions,
            services.BuildServiceProvider());

        return await service.AddReleaseDatabase(manifest, CancellationToken.None);
    }

    private sealed class StubHandler(Func<byte[]> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage { Content = new ByteArrayContent(body()) });
        }
    }

    private sealed class CapturingLogger : ILogger<FileHashesService>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }
}

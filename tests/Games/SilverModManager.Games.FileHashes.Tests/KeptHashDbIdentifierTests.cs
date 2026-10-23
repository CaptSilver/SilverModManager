using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.EpicGameStore.Values;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using NSubstitute;
using EpicBuildId = SilverModManager.Abstractions.EpicGameStore.Values.BuildId;
using OperatingSystem = SilverModManager.Abstractions.Games.FileHashes.Values.OperatingSystem;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// The game-hashes database is built and released outside this repository, so the attribute ids stored in it
/// are a contract with that release: GogBuild and EpicGameStoreBuild are written under the ids the upstream
/// project gave them. <c>Fixtures/game_hashes_db.zip</c> holds a database with one row of each, written under
/// those ids and zipped the way a release is, and must keep opening with the current models.
/// </summary>
public sealed class KeptHashDbIdentifierTests : IDisposable
{
    private const string Fixture = "Fixtures/game_hashes_db.zip";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kept-hash-db-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RealQueryEngineHost _host = new();
    private readonly List<IDisposable> _services = [];

    public KeptHashDbIdentifierTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        _host.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ReleaseShapedFixture_OpensAndReadsBackBothBuilds()
    {
        var service = await OpenFixture();
        var db = service.Current;

        var gog = GogBuild.All(db).Should().ContainSingle().Subject;
        gog.BuildId.Value.Should().Be(1234UL);
        gog.ProductId.Value.Should().Be(77UL);
        gog.ManifestId.Should().Be("kept-gog-manifest");
        gog.VersionName.Should().Be("kept-gog-version");
        gog.OperatingSystem.Should().Be(OperatingSystem.Windows);
        gog.Public.Should().BeTrue();

        var epic = EpicGameStoreBuild.All(db).Should().ContainSingle().Subject;
        epic.BuildId.Should().Be(EpicBuildId.From("kept-epic-build"));
        epic.ItemId.Should().Be(ItemId.From("kept-epic-item"));
        epic.ManifestHash.Should().Be(ManifestHash.From("kept-epic-manifest"));
        epic.AppName.Should().Be("KeptApp");
        epic.LabelName.Should().Be("Live-Windows");
        epic.BuildVersion.Should().Be("1.2.3");
    }

    [Fact]
    public async Task ReleaseShapedFixture_StoresTheKeptAttributeIds()
    {
        var service = await OpenFixture();

        var namespaces = service.Current.AttributeCache.AllAttributeIds.Select(id => id.Namespace).ToHashSet();

        namespaces.Should().Contain("NexusMods.Abstractions.Games.FileHashes.GogBuild");
        namespaces.Should().Contain("NexusMods.Stores.EpicGameStore.EpicGameStoreBuild");
    }

    /// <summary>Extracts the fixture the way the service lays out a downloaded release and opens it as the central database.</summary>
    private async Task<FileHashesService> OpenFixture()
    {
        var location = Path.Combine(_root, "databases");
        Directory.CreateDirectory(location);
        ZipFile.ExtractToDirectory(Path.Combine(AppContext.BaseDirectory, Fixture), Path.Combine(location, $"{Guid.NewGuid()}_1000"));

        var settings = new FileHashesServiceSettings
        {
            HashDatabaseLocation = new ConfigurablePath(KnownPath.TempDirectory, Path.GetRelativePath(Path.GetTempPath(), location)),
            LocalBaselineLocation = new ConfigurablePath(KnownPath.TempDirectory, Path.GetRelativePath(Path.GetTempPath(), Path.Combine(_root, "local-baseline"))),
            SkipUpdateCheck = true,
        };
        var settingsManager = Substitute.For<ISettingsManager>();
        settingsManager.Get<FileHashesServiceSettings>().Returns(settings);

        var service = new FileHashesService(NullLogger<FileHashesService>.Instance, settingsManager, FileSystem.Shared, new HttpClient(), new JsonSerializerOptions(), _host.Provider);
        _services.Add(service);
        await service.CheckForUpdate();
        return service;
    }
}

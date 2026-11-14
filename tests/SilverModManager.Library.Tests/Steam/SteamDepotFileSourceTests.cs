using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Library.Steam;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Steam;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;
using Xunit;

namespace SilverModManager.Library.Tests.Steam;

public class SteamDepotFileSourceTests(IServiceProvider serviceProvider, IConnection connection)
{
    private static readonly AppId App = AppId.From(262060);
    private static readonly DepotId Depot = DepotId.From(262061);
    private static readonly ManifestId ManifestNumber = ManifestId.From(7_000_000_000_001);

    // The library keeps one file per set of bytes and the tests share a database, so each test gets bytes of its own.
    private readonly byte[] _payload = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 3000).Select(i => (byte)(i * 5 + 1))).ToArray();
    private readonly string _fileName = $"file-{Guid.NewGuid():N}.dat";

    private readonly ISteamSession _session = Substitute.For<ISteamSession>();
    private readonly IDepotManifestSource _cache = Substitute.For<IDepotManifestCatalog>();
    private readonly ISteamDepotLocations _locations = Substitute.For<ISteamDepotLocations>();
    private readonly ISettingsManager _settings = Substitute.For<ISettingsManager>();

    private Sha1Value PayloadSha1 => Sha1Value.From(SHA1.HashData(_payload));
    private Sha256Value PayloadSha256 => Sha256Value.From(SHA256.HashData(_payload));
    private RelativePath PathInDepot => RelativePath.FromUnsanitizedInput($"Game/Data/{_fileName}");

    private SteamDepotFileSource Source()
    {
        _settings.Get<SteamDepotSettings>(Arg.Any<string?>()).Returns(new SteamDepotSettings { Branch = "beta" });
        return new SteamDepotFileSource(serviceProvider, _session, _cache, _locations, _settings);
    }

    private Manifest ManifestListing(Sha1Value hash) => new()
    {
        ManifestId = ManifestNumber,
        DepotId = Depot,
        CreationTime = DateTimeOffset.UnixEpoch,
        TotalCompressedSize = Size.Zero,
        TotalUncompressedSize = Size.FromLong(_payload.Length),
        Files = [new Manifest.FileData { Path = PathInDepot, Size = Size.FromLong(_payload.Length), Hash = hash, Chunks = [] }],
    };

    private void ServeManifest(Sha1Value listedHash, byte[]? bytes = null)
    {
        var manifest = ManifestListing(listedHash);
        _session.GetManifestContents(App, Depot, ManifestNumber, "beta", Arg.Any<CancellationToken>()).Returns(manifest);
        _session.GetFileStream(App, manifest, PathInDepot).Returns(_ => new MemoryStream(bytes ?? _payload));
    }

    private int LibraryCopies() => LibraryFile.All(connection.Db).Count(file => file.FileName == _fileName);

    [Fact]
    public async Task AMatchingSha1AddsTheFileWithItsSha256()
    {
        ServeManifest(PayloadSha1);

        var added = await Source().FetchFile(App, Depot, ManifestNumber, PathInDepot, PayloadSha1, UnassignedAcquisition.Instance, CancellationToken.None);

        added.FileName.Should().Be(_fileName);
        added.Sha256.Value.Should().Be(PayloadSha256);
        added.TryGetAsSteamDepotLibraryFile(out var depotFile).Should().BeTrue();
        depotFile.AppId.Should().Be(App.Value);
        depotFile.DepotId.Should().Be(Depot.Value);
        depotFile.ManifestId.Should().Be(ManifestNumber.Value);
        depotFile.Path.Should().Be(PathInDepot.ToString());
        depotFile.Sha1.Should().Be(PayloadSha1);
    }

    [Fact]
    public async Task AMismatchedSha1AddsNothing()
    {
        // The bytes are not what the manifest lists, as when a chunk is corrupted on the way.
        ServeManifest(PayloadSha1, bytes: [.. _payload.Reverse()]);

        var fetch = () => Source().FetchFile(App, Depot, ManifestNumber, PathInDepot, PayloadSha1, UnassignedAcquisition.Instance, CancellationToken.None);

        await fetch.Should().ThrowAsync<SteamDepotIntegrityException>();
        LibraryCopies().Should().Be(0);
    }

    [Fact]
    public async Task AnExpectedSha1ThatTheManifestDoesNotListIsRefusedBeforeAnyDownload()
    {
        ServeManifest(PayloadSha1);

        var fetch = () => Source().FetchFile(App, Depot, ManifestNumber, PathInDepot, Sha1Value.From(SHA1.HashData([1, 2, 3])), UnassignedAcquisition.Instance, CancellationToken.None);

        await fetch.Should().ThrowAsync<SteamDepotIntegrityException>();
        _session.DidNotReceiveWithAnyArgs().GetFileStream(default, default!, default);
        LibraryCopies().Should().Be(0);
    }

    [Fact]
    public async Task APathTheManifestDoesNotListIsRefusedInWords()
    {
        ServeManifest(PayloadSha1);

        var fetch = () => Source().FetchFile(App, Depot, ManifestNumber, RelativePath.FromUnsanitizedInput("Game/missing.dat"), null, UnassignedAcquisition.Instance, CancellationToken.None);

        await fetch.Should().ThrowAsync<SteamDepotFileNotFoundException>();
    }

    [Fact]
    public async Task APathInAnotherCaseIsTheSameFileAndNoExpectedHashTrustsTheManifest()
    {
        ServeManifest(PayloadSha1);

        var added = await Source().FetchFile(App, Depot, ManifestNumber, RelativePath.FromUnsanitizedInput(PathInDepot.ToString().ToUpperInvariant()), null, UnassignedAcquisition.Instance, CancellationToken.None);

        added.FileName.Should().Be(_fileName);
    }

    [Fact]
    public async Task FetchingWithoutAStoredLoginConnectsFirst()
    {
        ServeManifest(PayloadSha1);

        await Source().FetchFile(App, Depot, ManifestNumber, PathInDepot, PayloadSha1, UnassignedAcquisition.Instance, CancellationToken.None);

        Received.InOrder(() =>
        {
            _session.Connect(Arg.Any<CancellationToken>());
            _session.GetManifestContents(App, Depot, ManifestNumber, "beta", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task AHandlerGivenToTheFetchShowsTheLoginNotTheRegisteredOne()
    {
        ServeManifest(PayloadSha1);
        var handler = Substitute.For<IAuthInterventionHandler>();

        await Source().FetchFile(App, Depot, ManifestNumber, PathInDepot, PayloadSha1, UnassignedAcquisition.Instance, CancellationToken.None, handler);

        await _session.Received(1).Connect(handler, Arg.Any<CancellationToken>());
        await _session.DidNotReceive().Connect(Arg.Any<CancellationToken>());
    }

    private static readonly AbsolutePath SteamRoot = FileSystem.Shared.FromUnsanitizedFullPath("/steam");
    private static readonly AbsolutePath CachedManifest = FileSystem.Shared.FromUnsanitizedFullPath("/steam/depotcache/manifest.manifest");

    private void CacheHolds(ManifestFileHashes? hashes)
    {
        _locations.For(App).Returns([new SteamDepotLocation(SteamRoot, null)]);
        _cache.TryGetManifestPath(SteamRoot, ManifestNumber, null, out Arg.Any<AbsolutePath>()).Returns(call =>
        {
            call[3] = CachedManifest;
            return true;
        });
        ((IDepotManifestCatalog)_cache).ReadFileHashes(CachedManifest).Returns(hashes);
    }

    [Fact]
    public async Task VouchFromDepotcacheNeedsNoSession()
    {
        CacheHolds(new ManifestFileHashes(Depot, ManifestNumber, new HashSet<Sha1Value> { PayloadSha1 }));

        var vouched = await Source().VouchManifest(App, Depot, ManifestNumber, CancellationToken.None);

        vouched.Hashes.Should().BeEquivalentTo([PayloadSha1]);
        await _session.DidNotReceiveWithAnyArgs().Connect(default);
        await _session.DidNotReceiveWithAnyArgs().GetManifestContents(default, default, default, default!, default);
    }

    [Fact]
    public async Task EncryptedNamesStillVouchHashes()
    {
        // The reader that lists names has nothing to say about an encrypted manifest (its path is null), the hash reader does.
        CacheHolds(new ManifestFileHashes(Depot, ManifestNumber, new HashSet<Sha1Value> { PayloadSha1 }));
        ((IDepotManifestCatalog)_cache).ReadFileList(CachedManifest).Returns(new ManifestFileList(Depot, ManifestNumber, [new ManifestFile(null, 10, PayloadSha1)]));
        _cache.Read(CachedManifest).Returns(new LocalManifestResult.NeedsDepotKey(Depot, ManifestNumber));

        var vouched = await Source().VouchManifest(App, Depot, ManifestNumber, CancellationToken.None);

        vouched.Hashes.Should().Contain(PayloadSha1);
        await _session.DidNotReceiveWithAnyArgs().Connect(default);
    }

    [Fact]
    public async Task AManifestSteamDidNotCacheIsFetchedThroughTheLogin()
    {
        _locations.For(App).Returns([]);
        ServeManifest(PayloadSha1);

        var vouched = await Source().VouchManifest(App, Depot, ManifestNumber, CancellationToken.None);

        vouched.Hashes.Should().BeEquivalentTo([PayloadSha1]);
        Received.InOrder(() =>
        {
            _session.Connect(Arg.Any<CancellationToken>());
            _session.GetManifestContents(App, Depot, ManifestNumber, "beta", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ACachedManifestOfAnotherDepotDoesNotVouch()
    {
        CacheHolds(new ManifestFileHashes(DepotId.From(999), ManifestNumber, new HashSet<Sha1Value> { PayloadSha1 }));
        ServeManifest(PayloadSha1);

        await Source().VouchManifest(App, Depot, ManifestNumber, CancellationToken.None);

        await _session.Received(1).Connect(Arg.Any<CancellationToken>());
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels().AddOSInterop().AddFileExtractors();
        }
    }
}

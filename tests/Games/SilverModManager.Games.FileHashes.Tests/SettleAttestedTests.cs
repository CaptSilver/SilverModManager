using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// What <see cref="IFileHashesService.SettleAttested"/> does with the bytes of a file that was recorded from the
/// manifest: promote it when the SHA-1 agrees, keep the row and remember the bytes when it does not.
/// </summary>
public sealed class SettleAttestedTests : IDisposable
{
    private static readonly DateTimeOffset Modified = new(2026, 2, 16, 18, 0, 0, TimeSpan.Zero);

    private readonly ServiceHarness _harness = new();
    private readonly CapturingServiceLogger _logger = new();
    private FileHashesService _service = null!;
    private IConnection _local = null!;

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task ASha1ThatMatches_PromotesTheRowToPathAndHashRelations()
    {
        var (attested, baseline, manifest) = await Setup("a.esm", "vanilla");
        var hashes = await Hash("vanilla");

        await _service.SettleAttested(Installation(), [(attested, hashes, new FileFingerprint(hashes.Size, Modified))]);

        AttestedFile.All(_local.Db).Should().BeEmpty("a promoted row is gone");
        var path = LocalBaseline.Load(_local.Db, baseline).Files.Should().ContainSingle().Subject;
        path.Path.ToString().Should().Be("a.esm");
        path.Hash.XxHash3.Should().Be(hashes.XxHash3);
        path.Hash.Sha1.Should().Be(hashes.Sha1);
        SteamManifest.Load(_local.Db, manifest).Files.Should().ContainSingle().Which.Id.Should().Be(path.Id);
        var fingerprint = LocalFileHash.All(_local.Db).Should().ContainSingle().Subject;
        fingerprint.XxHash3.Should().Be(hashes.XxHash3);
        fingerprint.ModifiedUtc.Should().Be(Modified);
        HashRelation.FindByXxHash3(_local.Db, hashes.XxHash3).Should().ContainSingle("the relation is made once, not once for the path and again for the fingerprint");
    }

    [Fact]
    public async Task ASettleWaitsForTheSettleInProgress_SoARowIsPromotedOnce()
    {
        var (attested, baseline, _) = await Setup("a.esm", "vanilla");
        var hashes = await Hash("vanilla");
        var read = new (AttestedLookup, MultiHash, FileFingerprint)[] { (attested, hashes, new FileFingerprint(hashes.Size, Modified)) };

        // Stands in for a settle that has read the row and not yet written: a second one must not start reading.
        await AttestedFileRows.SettleTurn.WaitAsync(TestContext.Current.CancellationToken);
        Task second;
        try
        {
            second = Task.Run(() => _service.SettleAttested(Installation(), read), TestContext.Current.CancellationToken);
            var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));
            finished.Should().NotBe(second, "a settle that starts while another holds the turn waits for it");
            AttestedFile.All(_local.Db).Should().ContainSingle("the waiting settle has not touched the row");
        }
        finally
        {
            AttestedFileRows.SettleTurn.Release();
        }

        await second;
        LocalBaseline.Load(_local.Db, baseline).Files.Should().ContainSingle();
        PathHashRelation.All(_local.Db).Should().ContainSingle();
        HashRelation.FindByXxHash3(_local.Db, hashes.XxHash3).Should().ContainSingle();
    }

    [Fact]
    public async Task ASha1ThatDiffers_KeepsTheRowAndRemembersTheBytesAsAnExternalChange()
    {
        var (attested, baseline, _) = await Setup("a.esm", "vanilla");
        var edited = await Hash("VANILLA");

        await _service.SettleAttested(Installation(), [(attested, edited, new FileFingerprint(edited.Size, Modified.AddHours(1)))]);

        AttestedFile.All(_local.Db).Should().ContainSingle().Which.Sha1.Should().Be((await Hash("vanilla")).Sha1);
        LocalBaseline.Load(_local.Db, baseline).Files.Should().BeEmpty();
        LocalFileHash.All(_local.Db).Should().ContainSingle().Which.XxHash3.Should().Be(edited.XxHash3);
        HashRelation.FindByXxHash3(_local.Db, edited.XxHash3).Should().ContainSingle("a bare relation says the bytes were hashed in full");
        PathHashRelation.All(_local.Db).Should().BeEmpty();
        _logger.Lines.Should().ContainSingle(l => l.Contains("a.esm") && l.Contains("no longer matches the store's copy"));
    }

    [Fact]
    public async Task ARowThatIsGone_StillGetsTheFingerprintRowAndTheBytesRelation()
    {
        var (attested, baseline, _) = await Setup("a.esm", "vanilla");
        using (var tx = _local.BeginTransaction())
        {
            tx.Delete(attested.Id, recursive: false);
            await tx.Commit();
        }
        var hashes = await Hash("vanilla");

        await _service.SettleAttested(Installation(), [(attested, hashes, new FileFingerprint(hashes.Size, Modified))]);

        LocalBaseline.Load(_local.Db, baseline).Files.Should().BeEmpty("a rebuild replaced the baseline's rows; the new one resolves the path from the relation");
        LocalFileHash.All(_local.Db).Should().ContainSingle();
        HashRelation.FindByXxHash3(_local.Db, hashes.XxHash3).Should().ContainSingle();
        PathHashRelation.All(_local.Db).Should().BeEmpty();
    }

    [Fact]
    public void ResolverConvertsToTheSha1OfBytesTheAppHashed()
    {
        _service = _harness.CreateService(_harness.NewDatabaseLocation());
        _local = _service.Local.Connection;
        var hashes = Hash("vanilla").Result;
        using (var tx = _local.BeginTransaction())
        {
            _ = new HashRelation.New(tx)
            {
                MinimalHash = hashes.MinimalHash, XxHash3 = hashes.XxHash3, Sha1 = hashes.Sha1, XxHash64 = hashes.XxHash64,
                Md5 = hashes.Md5, Crc32 = hashes.Crc32, Size = hashes.Size,
            };
            tx.Commit().Wait();
        }

        var resolver = new RecordedDigestResolver(_service);
        resolver.TryConvert(FileIdentity.XxHash3(hashes.XxHash3), hashes.Size, HashKind.Sha1, out var sha1).Should().BeTrue();
        sha1.Should().Be(FileIdentity.Sha1(hashes.Sha1));
        resolver.TryConvert(FileIdentity.XxHash3(NexusMods.Hashing.xxHash3.Hash.From(1)), hashes.Size, HashKind.Sha1, out _).Should().BeFalse();
        resolver.TryConvert(FileIdentity.XxHash3(hashes.XxHash3), hashes.Size + Size.From(1), HashKind.Sha1, out _).Should().BeFalse("a row of another size is not these bytes");
    }

    private async Task<(AttestedLookup Attested, EntityId Baseline, EntityId Manifest)> Setup(string path, string content)
    {
        _service = _harness.CreateService(_harness.NewDatabaseLocation(), _logger);
        _local = _service.Local.Connection;
        await HashRows.AddBaselineOwningManifest(_local, LocalBaselineKind.DepotManifests, 1002);
        var id = await HashRows.AddAttestedFile(_local, 1002, path, content, Modified);
        var row = AttestedFile.Load(_local.Db, id);
        var lookup = new AttestedLookup(row.Id, row.Path, row.Sha1, row.Size, row.ModifiedUtc);
        return (lookup, row.BaselineId.Value, row.ManifestId.Value);
    }

    private static GameInstallation Installation()
    {
        var result = new GameLocatorResult
        {
            Game = Substitute.For<IGameData>(),
            Path = FileSystem.Shared.FromUnsanitizedFullPath("/games/test"),
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [LocatorId.From("1002")],
            Locator = Substitute.For<IGameLocator>(),
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, result.Path)));
    }

    private static Task<MultiHash> Hash(string content) => MultiHasher.HashStream(new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private sealed class CapturingServiceLogger : ILogger<FileHashesService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}

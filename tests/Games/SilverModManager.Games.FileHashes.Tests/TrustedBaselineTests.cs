using NexusMods.Hashing.xxHash3;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using OperatingSystem = SilverModManager.Abstractions.Games.FileHashes.Values.OperatingSystem;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// A settled Steam install whose files are older than the store's last write is taken as the manifest
/// describes it, with the hashes already stored for the same content; only the rest is read. Opens are
/// counted through the baseline's injectable file opener.
/// </summary>
public sealed class TrustedBaselineTests : IDisposable
{
    private static readonly DateTimeOffset InstallTime = new(2026, 2, 16, 18, 0, 0, TimeSpan.Zero);

    private readonly ServiceHarness _harness = new();
    private readonly string _gameRoot;
    private readonly IConnection _store;
    private readonly List<string> _opened = [];
    private readonly CapturingLogger _logger = new();

    public TrustedBaselineTests()
    {
        _gameRoot = Path.Combine(_harness.Root, "game");
        Directory.CreateDirectory(_gameRoot);
        _store = _harness.Host.OpenInMemory("local_hashes");
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task UntouchedInstall_IsBuiltWithoutOpeningAFileWhoseHashIsStored()
    {
        WriteFile("a.esm", "vanilla", InstallTime.AddDays(-1));
        WriteFile("b.esm", "other", InstallTime.AddDays(-1));
        var manifests = new[] { Manifest(1, ("a.esm", "vanilla"), ("b.esm", "other")) };
        await Build(manifests, trust: false);
        _opened.Clear();

        var result = await Build(manifests, trust: true);

        _opened.Should().BeEmpty();
        result.NonVanilla.Should().BeEmpty();
        var files = LocalBaseline.Load(_store.Db, result.BaselineId).Files.ToDictionary(f => f.Path.ToString());
        var expected = await Hash("vanilla");
        files["a.esm"].Hash.XxHash3.Should().Be(expected.XxHash3);
        files["a.esm"].Hash.Md5.Should().Be(expected.Md5);
        files["a.esm"].Hash.MinimalHash.Should().Be(expected.MinimalHash);
        files.Should().HaveCount(2);
    }

    [Fact]
    public async Task HashStoredInAnotherDatabase_IsUsedWithoutOpeningTheFile()
    {
        WriteFile("a.esm", "vanilla", InstallTime.AddDays(-1));
        var central = _harness.Host.OpenInMemory("central_hashes");
        await HashRows.AddSteamManifest(central, 7, ("a.esm", "vanilla"));

        var result = await Build([Manifest(1, ("a.esm", "vanilla"))], trust: true, extraDatabases: [central.Db]);

        _opened.Should().BeEmpty();
        var file = LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().ContainSingle().Subject;
        file.Hash.Sha1.Should().Be((await Hash("vanilla")).Sha1);
    }

    [Fact]
    public async Task FileModifiedAfterTheInstall_IsReadAndItsMismatchReported()
    {
        WriteFile("a.esm", "vanilla", InstallTime.AddDays(-1));
        WriteFile("b.esm", "other", InstallTime.AddDays(-1));
        var manifests = new[] { Manifest(1, ("a.esm", "vanilla"), ("b.esm", "other")) };
        await Build(manifests, trust: false);
        WriteFile("a.esm", "VANILLA", InstallTime.AddHours(1));
        _opened.Clear();

        var result = await Build(manifests, trust: true);

        _opened.Should().ContainSingle().Which.Should().EndWith("a.esm");
        result.NonVanilla.Should().ContainSingle().Which.Should().Match<NonVanillaEntry>(e =>
            e.Path.ToString() == "a.esm" && e.Reason == NonVanillaReason.HashDiffers);
        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().ContainSingle().Which.Path.ToString().Should().Be("b.esm");
    }

    [Fact]
    public async Task FileModifiedWithinTheTolerance_StaysTrusted()
    {
        WriteFile("a.esm", "vanilla", InstallTime.AddSeconds(1));
        var manifests = new[] { Manifest(1, ("a.esm", "vanilla")) };
        await Build(manifests, trust: false);
        _opened.Clear();

        await Build(manifests, trust: true);

        _opened.Should().BeEmpty();
    }

    [Fact]
    public async Task SizeMismatch_IsNonVanillaWithoutReading()
    {
        WriteFile("a.esm", "short", InstallTime.AddDays(-1));

        var result = await Build([Manifest(1, ("a.esm", "a longer vanilla file"))], trust: true);

        _opened.Should().BeEmpty();
        result.NonVanilla.Should().ContainSingle().Which.Reason.Should().Be(NonVanillaReason.SizeDiffers);
    }

    [Fact]
    public async Task UnsettledInstall_HashesEverything()
    {
        WriteFile("a.esm", "vanilla", InstallTime.AddDays(-1));
        var manifests = new[] { Manifest(1, ("a.esm", "vanilla")) };
        await Build(manifests, trust: false);
        _opened.Clear();

        await Build(manifests, trust: true, settled: false);

        _opened.Should().ContainSingle();
    }

    [Fact]
    public async Task UnknownInstallTime_HashesEverything()
    {
        WriteFile("a.esm", "vanilla", InstallTime.AddDays(-1));
        var manifests = new[] { Manifest(1, ("a.esm", "vanilla")) };
        await Build(manifests, trust: false);
        _opened.Clear();

        await Build(manifests, trust: true, knownInstallTime: false);

        _opened.Should().ContainSingle();
    }

    [Fact]
    public async Task TrustedFileWithoutAStoredHash_IsReadOnce()
    {
        WriteFile("a.esm", "vanilla", InstallTime.AddDays(-1));

        var result = await Build([Manifest(1, ("a.esm", "vanilla"))], trust: true);

        _opened.Should().ContainSingle().Which.Should().EndWith("a.esm");
        LocalBaseline.Load(_store.Db, result.BaselineId).Files.Should().ContainSingle()
            .Which.Hash.Sha1.Should().Be((await Hash("vanilla")).Sha1);
    }

    [Fact]
    public async Task StoredRowOfAnotherSize_IsNotTrusted()
    {
        // A row found by SHA-1 whose size disagrees describes some other content.
        WriteFile("a.esm", "vanilla", InstallTime.AddDays(-1));
        var manifests = new[] { Manifest(1, ("a.esm", "vanilla")) };
        using (var tx = _store.BeginTransaction())
        {
            var real = await Hash("vanilla");
            _ = new HashRelation.New(tx)
            {
                MinimalHash = real.MinimalHash, XxHash3 = real.XxHash3, Sha1 = real.Sha1, XxHash64 = real.XxHash64,
                Md5 = real.Md5, Crc32 = real.Crc32, Size = Size.From(999),
            };
            await tx.Commit();
        }

        await Build(manifests, trust: true);

        _opened.Should().ContainSingle();
    }

    [Fact]
    public async Task Rebuild_LogsHowManyFilesWereTrustedStoredAndRead()
    {
        WriteFile("a.esm", "vanilla", InstallTime.AddDays(-1));
        await Build([Manifest(1, ("a.esm", "vanilla"))], trust: false);
        WriteFile("b.esm", "other", InstallTime.AddDays(-1));
        _logger.Lines.Clear();

        await Build([Manifest(1, ("a.esm", "vanilla"), ("b.esm", "other"))], trust: true);

        _logger.Lines.Should().ContainSingle(l =>
            l.Contains("2 files vouched for by the store") && l.Contains("1 with stored hashes") && l.Contains("1 read because no hash was stored"));
    }

    [Fact]
    public async Task Build_WritesAFingerprintRowForAFileItTookFromTheStore()
    {
        WriteFile("a.esm", "vanilla", InstallTime.AddDays(-1));
        WriteFile("b.esm", "other", InstallTime.AddDays(1));
        var manifests = new[] { Manifest(1, ("a.esm", "vanilla"), ("b.esm", "other")) };
        await Build([manifests[0]], trust: false);
        foreach (var row in LocalFileHash.All(_store.Db).ToList())
        {
            using var tx = _store.BeginTransaction();
            tx.Delete(row.Id, recursive: false);
            await tx.Commit();
        }
        _opened.Clear();

        await Build(manifests, trust: true);

        // a.esm is vouched for by the store and never opened; b.esm is newer than the store's last write and is read.
        _opened.Should().ContainSingle().Which.Should().EndWith("b.esm");
        // Both get a row, so the index that follows finds a.esm's hash by fingerprint instead of reading it.
        LocalFileHash.All(_store.Db).Select(r => r.Path.ToString()).Should().BeEquivalentTo(["a.esm", "b.esm"]);
        var taken = LocalFileHash.All(_store.Db).Single(r => r.Path.ToString() == "a.esm");
        var info = new FileInfo(Path.Combine(_gameRoot, "a.esm"));
        taken.XxHash3.Should().Be(Encoding.UTF8.GetBytes("vanilla").xxHash3(), "the hash comes from the relation the store already held");
        taken.Size.Should().Be(Size.FromLong(info.Length));
        taken.ModifiedUtc.UtcTicks.Should().Be(info.LastWriteTimeUtc.Ticks);
    }

    private Task<BaselineBuild> Build(Manifest[] manifests, bool trust, bool settled = true, bool knownInstallTime = true, IDb[]? extraDatabases = null)
    {
        var baseline = new LocalVanillaBaseline(FileSystem.Shared, _logger, p =>
        {
            _opened.Add(p.ToString());
            return p.Read();
        }, extraDatabases is null ? null : () => extraDatabases);
        return baseline.Build(new BaselineRequest
        {
            GameRoot = FileSystem.Shared.FromUnsanitizedFullPath(_gameRoot),
            Store = GameStore.Steam,
            AppId = AppId.From(1),
            GamePath = "/games/test",
            LocatorIds = manifests.Select(m => m.ManifestId.Value.ToString()).ToArray(),
            VersionName = "1000",
            Manifests = manifests,
            AllInstalledDepotsCovered = true,
            IsIgnored = _ => false,
            Game = HashRows.OwnGameId,
            NexusGameId = HashRows.GameId,
            OperatingSystem = OperatingSystem.Windows,
            StoreSettled = trust && settled,
            StoreLastWrite = trust && knownInstallTime ? InstallTime : null,
        }, _store);
    }

    private string WriteFile(string relative, string content, DateTimeOffset modified)
    {
        var path = Path.Combine(_gameRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _gameRoot);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    private static Manifest Manifest(ulong id, params (string Path, string Content)[] files) => new()
    {
        ManifestId = ManifestId.From(id),
        DepotId = DepotId.From(2),
        CreationTime = DateTimeOffset.UnixEpoch,
        TotalCompressedSize = Size.Zero,
        TotalUncompressedSize = Size.Zero,
        Files = files.Select(f => new Manifest.FileData
        {
            Path = RelativePath.FromUnsanitizedInput(f.Path),
            Size = Size.FromLong(Encoding.UTF8.GetByteCount(f.Content)),
            Hash = Sha1Value.From(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(f.Content))),
            Chunks = [],
        }).ToArray(),
    };

    private static Task<MultiHash> Hash(string content) => MultiHasher.HashStream(new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private sealed class CapturingLogger : ILogger<LocalVanillaBaseline>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}

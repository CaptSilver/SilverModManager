using System.Collections.Immutable;
using System.Text;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Steam;
using NexusMods.Abstractions.Steam.DTOs;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NSubstitute;

namespace NexusMods.Games.FileHashes.Tests;

/// <summary>
/// The decisions about when a local baseline is built, kept and rebuilt. Games, Steam roots and manifest
/// files are made in a temp directory; the manifest source is a fake, so nothing reads a real Steam directory.
/// </summary>
public sealed class EnsureLocalBaselineTests : IDisposable
{
    private readonly ServiceHarness _harness = new();
    private readonly string _gameRoot;
    private readonly string _storeRoot;
    private readonly IConnection _store;
    private readonly FakeManifestSource _source;
    private readonly LocalBaselineCoordinator _coordinator;

    public EnsureLocalBaselineTests()
    {
        _gameRoot = Path.Combine(_harness.Root, "game");
        _storeRoot = Path.Combine(_harness.Root, "steam");
        Directory.CreateDirectory(_gameRoot);
        Directory.CreateDirectory(_storeRoot);
        _store = _harness.Host.OpenInMemory("local_hashes");
        _source = new FakeManifestSource(_storeRoot);
        _coordinator = new LocalBaselineCoordinator(
            () => _store,
            new LocalVanillaBaseline(FileSystem.Shared, NullLogger<LocalVanillaBaseline>.Instance),
            _source,
            NullLogger<LocalBaselineCoordinator>.Instance);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task FirstCall_BuildsABaselineFromTheManifests()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));
        var installation = Installation("101");

        var result = await _coordinator.Ensure(installation);

        result.Rebuilt.Should().BeTrue();
        result.Kind.Should().Be(LocalBaselineKind.DepotManifests);
        result.NonVanillaCount.Should().Be(0);
        var baseline = LocalBaseline.All(_store.Db).Should().ContainSingle().Subject;
        baseline.GamePath.Should().Be(installation.Locations[LocationId.Game].Path.ToString());
        baseline.VersionName.Should().Be("build-1");
        baseline.Files.Should().ContainSingle().Which.Path.ToString().Should().Be("a.esm");
    }

    [Fact]
    public async Task UnchangedLocatorIds_DoNotRebuild()
    {
        WriteGameFile("a.esm", "vanilla");
        WriteGameFile("mod.esp", "mod");
        _source.Add(101, ("a.esm", "vanilla"));
        await _coordinator.Ensure(Installation("101"));
        _source.Reads.Clear();

        var result = await _coordinator.Ensure(Installation("101"));

        result.Rebuilt.Should().BeFalse();
        result.Kind.Should().Be(LocalBaselineKind.DepotManifests);
        result.NonVanillaCount.Should().Be(1, "the stored report is returned without reading the game directory again");
        _source.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task LocatorIdsInAnotherOrder_DoNotRebuild()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));
        _source.Add(102, ("b.esm", "other"));
        await _coordinator.Ensure(Installation("101", "102"));
        _source.Reads.Clear();

        var result = await _coordinator.Ensure(Installation("102", "101"));

        result.Rebuilt.Should().BeFalse();
        _source.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangedLocatorIds_Rebuild()
    {
        WriteGameFile("a.esm", "vanilla v2");
        _source.Add(101, ("a.esm", "vanilla"));
        _source.Add(102, ("a.esm", "vanilla v2"));
        var first = await _coordinator.Ensure(Installation("101"));
        first.NonVanillaCount.Should().Be(1, "the file differs from the first build's manifest");

        var result = await _coordinator.Ensure(Installation("102"));

        result.Rebuilt.Should().BeTrue();
        result.NonVanillaCount.Should().Be(0);
        var baseline = LocalBaseline.All(_store.Db).Should().ContainSingle("the previous baseline's rows are replaced").Subject;
        baseline.LocatorIds.Should().Equal("102");
        NonVanillaFile.All(_store.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task ManifestNeedingADepotKey_CountsAsUncovered()
    {
        WriteGameFile("a.esm", "vanilla");
        WriteGameFile("encrypted-depot.bin", "from the depot without readable names");
        _source.Add(101, ("a.esm", "vanilla"));
        _source.AddNeedingKey(102);

        var result = await _coordinator.Ensure(Installation("101", "102"));

        result.Kind.Should().Be(LocalBaselineKind.PartialDepotManifests);
        result.NonVanillaCount.Should().Be(0);
        NonVanillaFile.All(_store.Db).Should().BeEmpty();
        LocalBaseline.All(_store.Db).Single().Files.Select(f => f.Path.ToString())
            .Should().BeEquivalentTo(["a.esm", "encrypted-depot.bin"]);
    }

    [Fact]
    public async Task UnreadableManifest_CountsAsUncovered()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));
        _source.AddUnreadable(102);

        var result = await _coordinator.Ensure(Installation("101", "102"));

        result.Kind.Should().Be(LocalBaselineKind.PartialDepotManifests);
    }

    [Fact]
    public async Task NonNumericLocatorId_CountsAsUncovered()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));

        var result = await _coordinator.Ensure(Installation("101", "not-a-manifest-id"));

        result.Kind.Should().Be(LocalBaselineKind.PartialDepotManifests);
    }

    [Fact]
    public async Task LocatorIdWithoutACachedManifest_CountsAsUncovered()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));

        var result = await _coordinator.Ensure(Installation("101", "999"));

        result.Kind.Should().Be(LocalBaselineKind.PartialDepotManifests);
    }

    [Fact]
    public async Task NoManifestSource_ProducesASnapshot()
    {
        WriteGameFile("a.esm", "one");
        WriteGameFile("Data/b.esm", "two");
        // The harness provider has no Steam services, so the service finds no manifest source.
        var service = _harness.CreateService(_harness.NewDatabaseLocation());

        var result = await service.EnsureLocalBaseline(Installation("101"));

        result.Kind.Should().Be(LocalBaselineKind.Snapshot);
        result.Rebuilt.Should().BeTrue();
        LocalBaseline.All(service.Local).Single().Files.Select(f => f.Path.ToString())
            .Should().BeEquivalentTo(["a.esm", "Data/b.esm"]);
    }

    [Fact]
    public async Task NonSteamInstallation_ProducesASnapshotWithoutAskingTheSource()
    {
        WriteGameFile("a.esm", "one");
        _source.Add(101, ("a.esm", "one"));

        var result = await _coordinator.Ensure(Make(["101"], store: GameStore.GOG));

        result.Kind.Should().Be(LocalBaselineKind.Snapshot);
        _source.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task SteamInstallationWithoutAStoreRoot_ProducesASnapshot()
    {
        WriteGameFile("a.esm", "one");
        _source.Add(101, ("a.esm", "one"));

        var result = await _coordinator.Ensure(Make(["101"], withStoreRoot: false));

        result.Kind.Should().Be(LocalBaselineKind.Snapshot);
        _source.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentCalls_ShareOneBuild()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));
        _source.Add(102, ("b.esm", "other"));

        var first = _coordinator.Ensure(Installation("101", "102"));
        var second = _coordinator.Ensure(Installation("101", "102"));
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(r => r.Rebuilt && r.Kind == LocalBaselineKind.DepotManifests);
        _source.Reads.Should().HaveCount(2, "one read per manifest, not per caller");
        LocalBaseline.All(_store.Db).Should().ContainSingle();
    }

    [Fact]
    public async Task UpdatePendingInSteam_DoesNotBuild()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));

        var result = await _coordinator.Ensure(Make(["101"], updatePending: true));

        result.Rebuilt.Should().BeFalse();
        result.Kind.Should().Be(LocalBaselineKind.Snapshot);
        _source.Reads.Should().BeEmpty();
        LocalBaseline.All(_store.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdatePendingInSteam_KeepsTheExistingBaseline()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));
        _source.Add(102, ("a.esm", "vanilla"));
        await _coordinator.Ensure(Installation("101"));
        _source.Reads.Clear();

        var result = await _coordinator.Ensure(Make(["102"], updatePending: true));

        result.Rebuilt.Should().BeFalse();
        result.Kind.Should().Be(LocalBaselineKind.DepotManifests);
        _source.Reads.Should().BeEmpty();
        LocalBaseline.All(_store.Db).Single().LocatorIds.Should().Equal("101");
    }

    [Fact]
    public async Task ManifestAppearingLater_UpgradesAPartialBaseline()
    {
        WriteGameFile("a.esm", "vanilla");
        WriteGameFile("b.esm", "other");
        _source.Add(101, ("a.esm", "vanilla"));
        var first = await _coordinator.Ensure(Installation("101", "102"));
        first.Kind.Should().Be(LocalBaselineKind.PartialDepotManifests);

        // Steam caches the second depot's manifest after the first build.
        await Task.Delay(50);
        _source.Add(102, ("b.esm", "other"));
        var second = await _coordinator.Ensure(Installation("101", "102"));

        second.Rebuilt.Should().BeTrue();
        second.Kind.Should().Be(LocalBaselineKind.DepotManifests);
    }

    [Fact]
    public async Task ManifestThatStillNeedsAKey_DoesNotRebuildAPartialBaselineEveryTime()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));
        _source.AddNeedingKey(102);
        await _coordinator.Ensure(Installation("101", "102"));
        _source.Reads.Clear();

        var second = await _coordinator.Ensure(Installation("101", "102"));

        second.Rebuilt.Should().BeFalse("the manifest that needs a key was cached before the build and still cannot be read");
        second.Kind.Should().Be(LocalBaselineKind.PartialDepotManifests);
        _source.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task ManifestAppearingLater_UpgradesASnapshot()
    {
        WriteGameFile("a.esm", "vanilla");
        var first = await _coordinator.Ensure(Installation("101"));
        first.Kind.Should().Be(LocalBaselineKind.Snapshot);

        await Task.Delay(50);
        _source.Add(101, ("a.esm", "vanilla"));
        var second = await _coordinator.Ensure(Installation("101"));

        second.Rebuilt.Should().BeTrue();
        second.Kind.Should().Be(LocalBaselineKind.DepotManifests);
    }

    [Fact]
    public async Task PathsTheSynchronizerFilters_AreLeftOutOfTheBaseline()
    {
        WriteGameFile("a.esm", "vanilla");
        WriteGameFile("saves/slot1.sav", "player data");
        WriteGameFile("backup.bak", "backup");
        _source.Add(101, ("a.esm", "vanilla"));
        var installation = Make(["101"],
            filter: p => p.Path.ToString().StartsWith("saves", StringComparison.Ordinal),
            ignoredBackup: p => p.Path.ToString() == "backup.bak");

        var result = await _coordinator.Ensure(installation);

        result.NonVanillaCount.Should().Be(0);
        NonVanillaFile.All(_store.Db).Should().BeEmpty();
    }

    private GameInstallation Installation(params string[] locatorIds) => Make(locatorIds);

    private GameInstallation Make(
        string[] locatorIds,
        GameStore? store = null,
        bool updatePending = false,
        bool withStoreRoot = true,
        Func<GamePath, bool>? filter = null,
        Func<GamePath, bool>? ignoredBackup = null)
    {
        var synchronizer = Substitute.For<ILoadoutSynchronizer>();
        synchronizer.GamePathFilter.Returns(filter is null ? GamePathFilters.Empty : GamePathFilters.Create(filter));
        synchronizer.IsIgnoredBackupPath(Arg.Any<GamePath>()).Returns(call => ignoredBackup?.Invoke(call.Arg<GamePath>()) ?? false);

        var game = Substitute.For<IGame>();
        game.Synchronizer.Returns(synchronizer);
        game.NexusModsGameId.Returns(Optional.Some(HashRows.GameId));

        var gamePath = FileSystem.Shared.FromUnsanitizedFullPath(_gameRoot);
        var result = new GameLocatorResult
        {
            Game = game,
            Path = gamePath,
            Store = store ?? GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [..locatorIds.Select(LocatorId.From)],
            Locator = Substitute.For<IGameLocator>(),
            StoreRoot = withStoreRoot ? FileSystem.Shared.FromUnsanitizedFullPath(_storeRoot) : null,
            StoreBuildId = "build-1",
            StoreUpdatePending = updatePending,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gamePath)));
    }

    private void WriteGameFile(string relative, string content)
    {
        var path = Path.Combine(_gameRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Resolves manifest ids to files in a temp directory and records which ones were read. A manifest
    /// resolves from the moment it is added, and its file is written then, so the file's modification
    /// time says when "Steam" cached it.
    /// </summary>
    private sealed class FakeManifestSource(string root) : IDepotManifestSource
    {
        private readonly Dictionary<ManifestId, LocalManifestResult> _results = new();
        private readonly Dictionary<string, ManifestId> _byPath = new();

        public List<ManifestId> Reads { get; } = [];

        public void Add(ulong id, params (string Path, string Content)[] files)
        {
            var manifestId = ManifestId.From(id);
            Register(manifestId, new LocalManifestResult.Parsed(new Manifest
            {
                ManifestId = manifestId,
                DepotId = DepotId.From((uint)id),
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
            }));
        }

        public void AddNeedingKey(ulong id) => Register(ManifestId.From(id), new LocalManifestResult.NeedsDepotKey(DepotId.From((uint)id), ManifestId.From(id)));

        public void AddUnreadable(ulong id) => Register(ManifestId.From(id), new LocalManifestResult.Unreadable("truncated"));

        private void Register(ManifestId id, LocalManifestResult result)
        {
            var path = PathOf(id);
            File.WriteAllText(path, "fake");
            _results[id] = result;
            _byPath[path] = id;
        }

        private string PathOf(ManifestId id) => Path.Combine(root, id.Value + ".manifest");

        public bool TryGetManifestPath(AbsolutePath steamRoot, ManifestId manifestId, AbsolutePath? installPath, out AbsolutePath manifestPath)
        {
            if (_results.ContainsKey(manifestId))
            {
                manifestPath = FileSystem.Shared.FromUnsanitizedFullPath(PathOf(manifestId));
                return true;
            }

            manifestPath = default;
            return false;
        }

        public LocalManifestResult Read(AbsolutePath manifestPath)
        {
            var id = _byPath[manifestPath.ToString()];
            lock (Reads) Reads.Add(id);
            return _results[id];
        }
    }
}

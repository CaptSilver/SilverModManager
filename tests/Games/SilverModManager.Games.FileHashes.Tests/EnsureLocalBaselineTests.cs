using System.Collections.Immutable;
using System.Text;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.NexusModsApi;
using NSubstitute;

namespace SilverModManager.Games.FileHashes.Tests;

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
    public async Task Remove_DeletesEveryRowOfTheBaselineAtThatPathAndNoOthers()
    {
        WriteGameFile("a.esm", "vanilla");
        WriteGameFile("mod.esp", "mod");
        _source.Add(101, ("a.esm", "vanilla"));
        await _coordinator.Ensure(Installation("101"));
        var other = await HashRows.AddLocalBaseline(_store, GameStore.Steam, "/games/other", LocalBaselineKind.Snapshot, [("b.esm", "other")], "c.esm");
        var gamePath = Installation("101").Locations[LocationId.Game].Path.ToString();

        await _coordinator.Remove(GameStore.Steam, gamePath);

        var left = LocalBaseline.All(_store.Db).Should().ContainSingle().Subject;
        left.Id.Should().Be(other);
        NonVanillaFile.All(_store.Db).Should().ContainSingle().Which.BaselineId.Value.Should().Be(other.Value);
        PathHashRelation.All(_store.Db).Should().ContainSingle("only the other baseline's path row is left");
        VersionDefinition.All(_store.Db).Should().BeEmpty();
        SteamManifest.All(_store.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_NothingAtThatPathOrStore_ChangesNothing()
    {
        var baseline = await HashRows.AddLocalBaseline(_store, GameStore.Steam, "/games/test", LocalBaselineKind.Snapshot, [("a.esm", "one")]);

        await _coordinator.Remove(GameStore.Steam, "/games/elsewhere");
        await _coordinator.Remove(GameStore.GOG, "/games/test");

        LocalBaseline.All(_store.Db).Should().ContainSingle().Which.Id.Should().Be(baseline);
    }

    [Fact]
    public async Task FirstCall_RecordsTheGamesOwnIdAndItsNexusId()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));

        await _coordinator.Ensure(Installation("101"));

        var baseline = LocalBaseline.All(_store.Db).Should().ContainSingle().Subject;
        baseline.Game.Value.Should().Be(HashRows.OwnGameId);
        VersionDefinition.All(_store.Db).Should().ContainSingle().Which.GameId.Value.Should().Be(HashRows.GameId);
    }

    [Fact]
    public async Task GameWithoutNexusId_BuildsABaselineThatCarriesNoNexusId()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));

        var result = await _coordinator.Ensure(Make(["101"], withNexusId: false));

        result.Rebuilt.Should().BeTrue();
        var baseline = LocalBaseline.All(_store.Db).Should().ContainSingle().Subject;
        baseline.Game.Value.Should().Be(HashRows.OwnGameId);
        var version = VersionDefinition.All(_store.Db).Should().ContainSingle().Subject;
        version.GameId.HasValue.Should().BeFalse("a made-up Nexus id would file the version under another game's id");
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
        _source.Add(101, ("a.esm", "vanilla"));
        var installation = Make(["101"], filter: p => p.Path.ToString().StartsWith("saves", StringComparison.Ordinal));

        var result = await _coordinator.Ensure(installation);

        result.NonVanillaCount.Should().Be(0);
        NonVanillaFile.All(_store.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task PathsExcludedOnlyFromBackups_AreStillClassified()
    {
        // A game that keeps its install out of backups (or only its archives) still ships those files.
        WriteGameFile("a.esm", "vanilla");
        WriteGameFile("Data/big.bsa", "archive");
        WriteGameFile("mod.esp", "mod");
        _source.Add(101, ("a.esm", "vanilla"), ("Data/big.bsa", "archive"));
        var installation = Make(["101"], ignoredBackup: _ => true);

        var result = await _coordinator.Ensure(installation);

        result.NonVanillaCount.Should().Be(1);
        var baseline = LocalBaseline.All(_store.Db).Should().ContainSingle().Subject;
        baseline.Files.Select(f => f.Path.ToString()).Should().BeEquivalentTo(["a.esm", "Data/big.bsa"]);
        NonVanillaFile.All(_store.Db).Should().ContainSingle().Which.Path.ToString().Should().Be("mod.esp");
    }

    [Fact]
    public async Task BaselineThatClassifiedNothing_IsRebuilt()
    {
        _source.Add(101, ("a.esm", "vanilla"));
        var installation = Installation("101");
        (await _coordinator.Ensure(installation)).Kind.Should().Be(LocalBaselineKind.DepotManifests);
        LocalBaseline.All(_store.Db).Should().ContainSingle().Which.Files.Should().BeEmpty();
        WriteGameFile("a.esm", "vanilla");

        var second = await _coordinator.Ensure(installation);

        second.Rebuilt.Should().BeTrue("an empty classification is never a finished baseline");
        LocalBaseline.All(_store.Db).Should().ContainSingle().Which.Files.Should().ContainSingle();
    }

    [Fact]
    public async Task DepotBaselineBuiltUnderOlderClassificationRules_IsRebuilt()
    {
        WriteGameFile("a.esm", "vanilla");
        WriteGameFile("Data/big.bsa", "archive");
        _source.Add(101, ("a.esm", "vanilla"), ("Data/big.bsa", "archive"));

        // An earlier build skipped archives, so the manifest-listed .bsa is in neither the vanilla files nor the report.
        await _coordinator.Ensure(Make(["101"], filter: path => path.Extension == new Extension(".bsa")));
        LocalBaseline.All(_store.Db).Should().ContainSingle().Which.Files.Select(f => f.Path.ToString()).Should().BeEquivalentTo(["a.esm"]);
        await ForgetClassificationVersion();

        var result = await _coordinator.Ensure(Installation("101"));

        result.Rebuilt.Should().BeTrue("a baseline without the current rule version cannot be trusted");
        LocalBaseline.All(_store.Db).Should().ContainSingle().Which.Files.Select(f => f.Path.ToString())
            .Should().BeEquivalentTo(["a.esm", "Data/big.bsa"]);
    }

    [Fact]
    public async Task DepotBaselineBuiltBeforeCreationRecordsWereFiltered_IsRebuiltWithoutThem()
    {
        WriteGameFile("a.esm", "vanilla");
        WriteGameFile("Creations/x-1.manifest", "written by the game");
        _source.Add(101, ("a.esm", "vanilla"));

        // The first build still counted the game's own Creations record as a stray file.
        await _coordinator.Ensure(Installation("101"));
        NonVanillaFile.All(_store.Db).Should().ContainSingle().Which.Path.ToString().Should().Be("Creations/x-1.manifest");
        await SetClassificationVersion(1);

        var result = await _coordinator.Ensure(Make(["101"], filter: path => path.Path.ToString().StartsWith("Creations/", StringComparison.Ordinal)));

        result.Rebuilt.Should().BeTrue("a version 1 baseline predates the filter on the Creations folder");
        result.NonVanillaCount.Should().Be(0);
        NonVanillaFile.All(_store.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task DepotBaselineBuiltUnderTheCurrentRules_IsKept()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));
        await _coordinator.Ensure(Installation("101"));

        var result = await _coordinator.Ensure(Installation("101"));

        result.Rebuilt.Should().BeFalse();
        LocalBaseline.All(_store.Db).Single().ClassificationVersion.Value.Should().Be(LocalVanillaBaseline.ClassificationVersion);
    }

    [Fact]
    public async Task SnapshotBuiltUnderOlderClassificationRules_IsKept()
    {
        WriteGameFile("a.esm", "vanilla");
        await _coordinator.Ensure(Make(["101"], store: GameStore.GOG));
        await ForgetClassificationVersion();

        // A rebuild now would take the mod the app deployed since for one of the game's own files.
        WriteGameFile("Data/mod.esp", "deployed");
        var result = await _coordinator.Ensure(Make(["101"], store: GameStore.GOG));

        result.Rebuilt.Should().BeFalse("a snapshot takes the folder as it stands, mods included");
        LocalBaseline.All(_store.Db).Single().Files.Select(f => f.Path.ToString()).Should().BeEquivalentTo(["a.esm"]);
    }

    [Fact]
    public async Task PartialBaselineBuiltUnderOlderClassificationRules_IsKept()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));
        await _coordinator.Ensure(Installation("101", "999"));
        await ForgetClassificationVersion();

        WriteGameFile("Data/mod.esp", "deployed");
        var result = await _coordinator.Ensure(Installation("101", "999"));

        result.Rebuilt.Should().BeFalse("a partial baseline takes every file no manifest lists as vanilla, mods included");
        LocalBaseline.All(_store.Db).Single().Files.Select(f => f.Path.ToString()).Should().BeEquivalentTo(["a.esm"]);
    }

    [Fact]
    public async Task WaitForBuild_ReturnsOnlyOnceABuildInFlightHasCommitted()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var coordinator = new LocalBaselineCoordinator(
            () => _store,
            new LocalVanillaBaseline(FileSystem.Shared, NullLogger<LocalVanillaBaseline>.Instance, path =>
            {
                entered.Set();
                release.Wait();
                return path.Read();
            }),
            _source,
            NullLogger<LocalBaselineCoordinator>.Instance);
        var installation = Installation("101");
        var build = coordinator.Ensure(installation);
        entered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the build has to be hashing for there to be anything to wait for");

        var wait = coordinator.WaitForBuild(installation);
        await Task.Delay(100);
        wait.IsCompleted.Should().BeFalse("the build has not committed its rows yet");
        LocalBaseline.All(_store.Db).Should().BeEmpty();

        release.Set();
        await wait;

        LocalBaseline.All(_store.Db).Should().ContainSingle("the wait ends after the rows are committed");
        (await build).Rebuilt.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForBuild_WithNothingInFlight_ReturnsAtOnceAndBuildsNothing()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));

        await _coordinator.WaitForBuild(Installation("101"));

        LocalBaseline.All(_store.Db).Should().BeEmpty();
        _source.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task Rebuild_OfASettledInstallOlderThanTheStoresLastWrite_ReadsNoFile()
    {
        WriteGameFile("a.esm", "vanilla");
        _source.Add(101, ("a.esm", "vanilla"));
        await _coordinator.Ensure(Installation("101"));
        await ForgetClassificationVersion();
        var opened = 0;
        var coordinator = new LocalBaselineCoordinator(
            () => _store,
            new LocalVanillaBaseline(FileSystem.Shared, NullLogger<LocalVanillaBaseline>.Instance, path =>
            {
                Interlocked.Increment(ref opened);
                return path.Read();
            }),
            _source,
            NullLogger<LocalBaselineCoordinator>.Instance);

        var result = await coordinator.Ensure(Make(["101"], storeLastWrite: DateTimeOffset.UtcNow.AddMinutes(5)));

        result.Rebuilt.Should().BeTrue();
        opened.Should().Be(0, "Steam vouches for the file and its hash is already stored");
        LocalBaseline.All(_store.Db).Single().Files.Should().ContainSingle();
    }

    /// <summary>Makes the one baseline look like it was recorded before the classification rules were versioned.</summary>
    private async Task ForgetClassificationVersion()
    {
        var baseline = LocalBaseline.All(_store.Db).Should().ContainSingle().Subject;
        using var tx = _store.BeginTransaction();
        tx.Retract(baseline.Id, LocalBaseline.ClassificationVersion, baseline.ClassificationVersion.Value);
        await tx.Commit();
    }

    private async Task SetClassificationVersion(int version)
    {
        var baseline = LocalBaseline.All(_store.Db).Should().ContainSingle().Subject;
        // Two transactions: a retract and an add of the same value in one would cancel out.
        using (var retract = _store.BeginTransaction())
        {
            retract.Retract(baseline.Id, LocalBaseline.ClassificationVersion, baseline.ClassificationVersion.Value);
            await retract.Commit();
        }

        using var tx = _store.BeginTransaction();
        tx.Add(baseline.Id, LocalBaseline.ClassificationVersion, version);
        await tx.Commit();
    }

    private GameInstallation Installation(params string[] locatorIds) => Make(locatorIds);

    private GameInstallation Make(
        string[] locatorIds,
        GameStore? store = null,
        bool updatePending = false,
        bool withStoreRoot = true,
        DateTimeOffset? storeLastWrite = null,
        Func<GamePath, bool>? filter = null,
        Func<GamePath, bool>? ignoredBackup = null,
        bool withNexusId = true)
    {
        var synchronizer = Substitute.For<ILoadoutSynchronizer>();
        synchronizer.GamePathFilter.Returns(filter is null ? GamePathFilters.Empty : GamePathFilters.Create(filter));
        synchronizer.IsIgnoredBackupPath(Arg.Any<GamePath>()).Returns(call => ignoredBackup?.Invoke(call.Arg<GamePath>()) ?? false);

        var game = Substitute.For<SubstitutableGame>();
        game.Synchronizer.Returns(synchronizer);
        game.NexusModsGameId.Returns(withNexusId ? Optional.Some(HashRows.GameId) : Optional<NexusModsGameId>.None);

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
            StoreLastWrite = storeLastWrite,
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

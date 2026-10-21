using System.Collections.Immutable;
using System.Text;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.Steam;
using NexusMods.Networking.Steam.Tests;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Games.FileHashes.Tests;

/// <summary>
/// Baselines built from manifest files written the way the Steam client writes them, found by the real depotcache
/// lookup, in a library reached through a symlinked ancestor.
/// </summary>
public sealed class DepotcacheBaselineTests : IDisposable
{
    private const uint GameDepot = 1001;
    private const uint ContentDepot = 1002;
    private const ulong GameManifest = 5001;
    private const ulong ContentManifest = 5002;

    private readonly ServiceHarness _harness = new();
    private readonly AbsolutePath _steamRoot;
    private readonly AbsolutePath _library;
    private readonly AbsolutePath _gameRoot;

    public DepotcacheBaselineTests()
    {
        var root = FileSystem.Shared.FromUnsanitizedFullPath(_harness.Root);
        _steamRoot = root / "steam";
        (_steamRoot / "depotcache").CreateDirectory();

        (root / "real" / "SteamLibrary" / "steamapps" / "common" / "Game").CreateDirectory();
        Directory.CreateSymbolicLink((root / "link").ToString(), (root / "real").ToString());
        _library = root / "link" / "SteamLibrary" / "steamapps";
        _gameRoot = _library / "common" / "Game";
    }

    public void Dispose() => _harness.Dispose();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private void WriteManifest(AbsolutePath directory, uint depot, ulong manifest, params (string Path, string Content)[] files)
        => DepotManifestFixture.Write(directory / $"{depot}_{manifest}.manifest", DepotId.From(depot), ManifestId.From(manifest), files.Select(f => (f.Path, Bytes(f.Content))));

    private void WriteGameFile(string path, string content)
    {
        var target = _gameRoot / path;
        target.Parent.CreateDirectory();
        File.WriteAllText(target.ToString(), content);
    }

    private (LocalBaselineCoordinator Coordinator, IConnection Store) Coordinator(FileHashesService? service = null)
    {
        var store = service?.Local.Connection ?? _harness.Host.OpenInMemory("local_hashes");
        var coordinator = new LocalBaselineCoordinator(
            () => store,
            new LocalVanillaBaseline(FileSystem.Shared, NullLogger<LocalVanillaBaseline>.Instance),
            new LocalDepotManifests(NullLogger<LocalDepotManifests>.Instance),
            NullLogger<LocalBaselineCoordinator>.Instance);
        return (coordinator, store);
    }

    private GameInstallation Installation(params ulong[] manifests)
    {
        var synchronizer = Substitute.For<ILoadoutSynchronizer>();
        synchronizer.GamePathFilter.Returns(GamePathFilters.Empty);

        var game = Substitute.For<IGame>();
        game.Synchronizer.Returns(synchronizer);
        game.NexusModsGameId.Returns(Optional.Some(HashRows.GameId));

        var result = new GameLocatorResult
        {
            Game = game,
            Path = _gameRoot,
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [..manifests.Select(m => LocatorId.From(m.ToString()))],
            Locator = Substitute.For<IGameLocator>(),
            StoreRoot = _steamRoot,
            StoreBuildId = "1",
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, _gameRoot)));
    }

    [Fact]
    public async Task ManifestsWithBackslashPathsBuildABaselineWhoseFilesAreTheOnesOnDisk()
    {
        WriteGameFile("bin/game.exe", "executable");
        WriteGameFile("data/archive/main.archive", "archive");
        WriteGameFile("mods/added.archive", "a mod");
        WriteManifest(_steamRoot / "depotcache", GameDepot, GameManifest, ("bin/game.exe", "executable"), ("data/archive/main.archive", "archive"));
        var (coordinator, store) = Coordinator();

        var result = await coordinator.Ensure(Installation(GameManifest));

        result.Kind.Should().Be(LocalBaselineKind.DepotManifests);
        var baseline = LocalBaseline.All(store.Db).Should().ContainSingle().Subject;
        baseline.Files.Select(f => f.Path.ToString()).Should().BeEquivalentTo("bin/game.exe", "data/archive/main.archive");
        NonVanillaFile.FindByBaseline(store.Db, baseline.Id).Should().ContainSingle().Which.Path.ToString().Should().Be("mods/added.archive");
    }

    [Fact]
    public async Task AnOlderManifestOfTheSameDepotBesideTheCurrentOneIsNotUsed()
    {
        WriteGameFile("bin/game.exe", "current executable");
        WriteManifest(_steamRoot / "depotcache", GameDepot, 4000, ("bin/game.exe", "an older executable"));
        WriteManifest(_steamRoot / "depotcache", GameDepot, GameManifest, ("bin/game.exe", "current executable"));
        var (coordinator, store) = Coordinator();

        var result = await coordinator.Ensure(Installation(GameManifest));

        result.NonVanillaCount.Should().Be(0, "the file matches the manifest the install is at");
        LocalBaseline.All(store.Db).Single().Files.Should().ContainSingle().Which.Path.ToString().Should().Be("bin/game.exe");
    }

    [Fact]
    public async Task AManifestOnlyInTheLibrarysOwnDepotcacheCompletesTheBaseline()
    {
        WriteGameFile("bin/game.exe", "executable");
        WriteGameFile("dlc/extra.archive", "expansion");
        WriteManifest(_steamRoot / "depotcache", GameDepot, GameManifest, ("bin/game.exe", "executable"));
        WriteManifest(_library / "depotcache", ContentDepot, ContentManifest, ("dlc/extra.archive", "expansion"));
        var (coordinator, store) = Coordinator();

        var result = await coordinator.Ensure(Installation(GameManifest, ContentManifest));

        result.Kind.Should().Be(LocalBaselineKind.DepotManifests);
        LocalBaseline.All(store.Db).Single().Files.Select(f => f.Path.ToString()).Should().BeEquivalentTo("bin/game.exe", "dlc/extra.archive");
    }

    [Fact]
    public async Task ABuildTheCentralDatabaseHasNoRowsForIsCoveredOnceItsBaselineIsBuilt()
    {
        WriteGameFile("bin/game.exe", "executable");
        WriteManifest(_steamRoot / "depotcache", GameDepot, GameManifest, ("bin/game.exe", "executable"));
        var location = _harness.NewDatabaseLocation();
        await _harness.CreateDatabaseOnDisk(location, 1000, c => HashRows.AddSteamManifest(c, 9999, ("other.esm", "a build of another game")));
        var service = _harness.CreateService(location);
        await service.CheckForUpdate();
        var (coordinator, _) = Coordinator(service);

        service.CoversInstalledBuild((GameStore.Steam, LocatorIds(GameManifest))).Should().BeFalse("no store has a row for the build yet");
        await coordinator.Ensure(Installation(GameManifest));

        service.CoversInstalledBuild((GameStore.Steam, LocatorIds(GameManifest))).Should().BeTrue();
    }

    [Fact]
    public async Task ADepotWhoseManifestSteamNeverSavedLeavesTheBuildUncovered()
    {
        WriteGameFile("bin/game.exe", "executable");
        WriteManifest(_steamRoot / "depotcache", GameDepot, GameManifest, ("bin/game.exe", "executable"));
        var service = _harness.CreateService(_harness.NewDatabaseLocation());
        await service.CheckForUpdate();
        var (coordinator, _) = Coordinator(service);

        var result = await coordinator.Ensure(Installation(GameManifest, ContentManifest));

        result.Kind.Should().Be(LocalBaselineKind.PartialDepotManifests);
        service.CoversInstalledBuild((GameStore.Steam, LocatorIds(GameManifest, ContentManifest))).Should().BeFalse("the second depot has no rows anywhere");
    }

    [Fact]
    public async Task ACentralDatabaseWithRowsForTheBuildCoversItWithoutAnyBaseline()
    {
        var location = _harness.NewDatabaseLocation();
        await _harness.CreateDatabaseOnDisk(location, 1000, async c =>
        {
            await HashRows.AddSteamManifest(c, GameManifest, ("bin/game.exe", "executable"));
            await HashRows.AddSteamManifest(c, ContentManifest, ("dlc/extra.archive", "expansion"));
        });
        var service = _harness.CreateService(location);
        await service.CheckForUpdate();

        service.CoversInstalledBuild((GameStore.Steam, LocatorIds(GameManifest, ContentManifest))).Should().BeTrue();
    }

    private static LocatorId[] LocatorIds(params ulong[] manifests) => manifests.Select(m => LocatorId.From(m.ToString())).ToArray();
}

using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using NSubstitute;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// What happens to the file hash data and the Health Check when a game's install folder changed.
/// </summary>
public sealed class GameMovedTests : IDisposable
{
    private const string OldPath = "/home/steam/test";
    private const string NewPath = "/mnt/steam/test";

    private readonly RealQueryEngineHost _host = new();
    private readonly IConnection _main;
    private readonly IConnection _local;

    public GameMovedTests()
    {
        _main = _host.OpenInMemory("main");
        _local = _host.OpenInMemory("local_hashes");
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Emitter_RowMoved_NamesTheGameAndBothFolders()
    {
        var loadout = await MovedLoadout();

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.GameMovedMessageData>>().Subject.MessageData;
        data.Game.Should().Be("Test game");
        data.OldPath.Should().Be(OldPath);
        data.NewPath.Should().Be(NewPath);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        diagnostic.Actions.Should().ContainSingle().Which.Id.Should().Be(DiagnosticActions.DismissGameMovedId);
    }

    [Fact]
    public async Task Emitter_RowNeverMoved_YieldsNothing()
    {
        var id = await HashRows.AddLoadout(_main, GameStore.Steam, NewPath);

        (await Diagnose(Loadout.Load(_main.Db, id))).Should().BeEmpty();
    }

    [Fact]
    public async Task Emitter_AfterTheNoteIsDismissed_YieldsNothingAndStaysQuiet()
    {
        var loadout = await MovedLoadout();
        (await Diagnose(loadout)).Should().ContainSingle();

        using (var tx = _main.BeginTransaction())
        {
            tx.Retract(loadout.Installation.Id, GameInstallMetadata.MovedFrom, OldPath);
            await tx.Commit();
        }

        loadout = Loadout.Load(_main.Db, loadout.Id);
        (await Diagnose(loadout)).Should().BeEmpty();
        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task Emitter_TwoLoadoutsOnTheMovedInstall_EachShowTheNote()
    {
        var first = await MovedLoadout();
        using var tx = _main.BeginTransaction();
        _ = new Loadout.New(tx)
        {
            Name = "Second",
            ShortName = "S",
            InstallationId = first.InstallationId,
            Revision = 0,
            LoadoutKind = LoadoutKind.Default,
            GameVersion = VanityVersion.From("Unknown"),
        };
        await tx.Commit();

        (await Diagnose(first)).Should().ContainSingle();
    }

    [Fact]
    public async Task Pass_MovedGame_RemovesTheBaselineOfTheFolderItLeft()
    {
        var hashes = Substitute.For<IFileHashesService>();
        var (registry, installation) = await MovedInstallation(located: [NewPath]);

        await RunPass(hashes, registry, installation);

        await hashes.Received(1).RemoveLocalBaseline(GameStore.Steam, OldPath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pass_RowStillNamesTheFolderItLeft_RemovesThatBaseline()
    {
        // The registry records the new folder in the background, so the pass can get the row before it lands.
        var hashes = Substitute.For<IFileHashesService>();
        var id = await HashRows.AddLoadout(_main, GameStore.Steam, OldPath);
        var registry = Registry(Loadout.Load(_main.Db, id).Installation, NewPath, out var installation);

        await RunPass(hashes, registry, installation);

        RemovedBaselines(hashes).Should().Equal(OldPath);
    }

    [Fact]
    public async Task Pass_OldFolderIsAnotherLocatedInstall_KeepsItsBaseline()
    {
        var hashes = Substitute.For<IFileHashesService>();
        var (registry, installation) = await MovedInstallation(located: [NewPath, OldPath]);

        await RunPass(hashes, registry, installation);

        RemovedBaselines(hashes).Should().BeEmpty();
    }

    [Fact]
    public async Task Pass_GameNeverMoved_RemovesNothing()
    {
        var hashes = Substitute.For<IFileHashesService>();
        var id = await HashRows.AddLoadout(_main, GameStore.Steam, NewPath);
        var install = Loadout.Load(_main.Db, id).Installation;
        var registry = Registry(install, NewPath, out var installation);

        await RunPass(hashes, registry, installation);

        RemovedBaselines(hashes).Should().BeEmpty();
    }

    // Argument matchers cannot stand in for a GameStore, which has no default value.
    private static string[] RemovedBaselines(IFileHashesService hashes) => hashes.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IFileHashesService.RemoveLocalBaseline))
        .Select(call => (string)call.GetArguments()[1]!)
        .ToArray();

    private async Task<Loadout.ReadOnly> MovedLoadout()
    {
        var id = await HashRows.AddLoadout(_main, GameStore.Steam, NewPath);
        var loadout = Loadout.Load(_main.Db, id);
        using var tx = _main.BeginTransaction();
        tx.Add(loadout.Installation.Id, GameInstallMetadata.MovedFrom, OldPath);
        await tx.Commit();
        return Loadout.Load(_main.Db, id);
    }

    private async Task<(IGameRegistry Registry, GameInstallation Installation)> MovedInstallation(string[] located)
    {
        var loadout = await MovedLoadout();
        var registry = Registry(loadout.Installation, NewPath, out var installation, located);
        return (registry, installation);
    }

    private static async Task RunPass(IFileHashesService hashes, IGameRegistry registry, GameInstallation installation)
    {
        hashes.EnsureLocalBaseline(Arg.Any<GameInstallation>(), Arg.Any<CancellationToken>())
            .Returns(new LocalBaselineResult(LocalBaselineKind.Snapshot, false, 0));
        var pass = new LocalBaselineStartupPass(hashes, registry, NullLogger<LocalBaselineStartupPass>.Instance);
        await pass.Run(CancellationToken.None);
    }

    /// <summary>A registry that locates the given folders and knows <paramref name="row"/> as the metadata of the one at <paramref name="current"/>.</summary>
    private static IGameRegistry Registry(GameInstallMetadata.ReadOnly row, string current, out GameInstallation installation, string[]? located = null)
    {
        var all = (located ?? [current]).Select(Located).ToImmutableArray();
        installation = all.First(i => i.LocatorResult.Path.ToString() == current);
        var own = installation;

        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns(all);
        registry.TryGetMetadata(Arg.Any<GameInstallation>(), out Arg.Any<GameInstallMetadata.ReadOnly>()).Returns(call =>
        {
            var isOwn = ReferenceEquals(call.Arg<GameInstallation>(), own);
            call[1] = isOwn ? row : default(GameInstallMetadata.ReadOnly);
            return isOwn;
        });
        return registry;
    }

    private static GameInstallation Located(string folder)
    {
        var game = Substitute.For<SubstitutableGame>();
        game.NexusModsGameId.Returns(DynamicData.Kernel.Optional.Some(HashRows.GameId));
        var path = FileSystem.Shared.FromUnsanitizedFullPath(folder);
        var result = new GameLocatorResult
        {
            Game = game,
            Path = path,
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, path)));
    }

    private static async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var results = new List<Diagnostic>();
        await foreach (var diagnostic in new GameMovedEmitter().Diagnose(loadout, CancellationToken.None))
            results.Add(diagnostic);
        return results.ToArray();
    }
}

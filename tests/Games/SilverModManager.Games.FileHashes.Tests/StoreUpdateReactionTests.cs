using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// What the running app does when the store changes a registered game's build. The locator is a stand-in whose
/// answer the test changes; nothing reads a Steam folder or watches a file.
/// </summary>
public sealed class StoreUpdateReactionTests : IDisposable
{
    private const string GamePath = "/steam/steamapps/common/Test";

    private readonly RealQueryEngineHost _host = new();
    private readonly IConnection _main;
    private readonly IGameRegistry _registry = Substitute.For<IGameRegistry>();
    private readonly IFileHashesService _hashes = Substitute.For<IFileHashesService>();
    private readonly IDiagnosticManager _diagnostics = Substitute.For<IDiagnosticManager>();
    private readonly IGameLocator _locator = Substitute.For<IGameLocator>();
    private readonly StoreUpdateReaction _reaction;

    public StoreUpdateReactionTests()
    {
        _main = _host.OpenInMemory("main");
        _hashes.EnsureLocalBaseline(Arg.Any<GameInstallation>(), Arg.Any<CancellationToken>())
            .Returns(new LocalBaselineResult(LocalBaselineKind.DepotManifests, true, 0));
        _diagnostics.RefreshAsync(Arg.Any<LoadoutId>()).Returns(Task.CompletedTask);
        _reaction = new StoreUpdateReaction(_registry, _hashes, _diagnostics, _main, NullLogger<StoreUpdateReaction>.Instance);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Check_TheStoreHasNewIds_RefreshesTheRegistryTheBaselineAndEveryLoadoutOnThatInstall()
    {
        var first = await HashRows.AddLoadout(_main, GameStore.Steam, GamePath, "old");
        var other = await HashRows.AddLoadout(_main, GameStore.Steam, "/steam/steamapps/common/Elsewhere", "old");
        var installation = Installation("old", "build-1");
        var row = Loadout.Load(_main.Db, first).Installation;
        _registry.TryGetMetadata(Arg.Any<GameInstallation>(), out Arg.Any<GameInstallMetadata.ReadOnly>())
            .Returns(call => { call[1] = row; return true; });
        StoreSays(Result("new", "build-2"));

        var outcome = await _reaction.Check(installation, TestContext.Current.CancellationToken);

        outcome.Should().Be(StoreUpdateOutcome.Refreshed);
        _registry.Received(1).RefreshInstallation(Arg.Is<GameInstallation>(i => i.LocatorResult.LocatorIds.Single().Value == "new"));
        await _hashes.Received(1).EnsureLocalBaseline(Arg.Is<GameInstallation>(i => i.LocatorResult.StoreBuildId == "build-2"), Arg.Any<CancellationToken>());
        await _diagnostics.Received(1).RefreshAsync(Loadout.Load(_main.Db, first).LoadoutId);
        await _diagnostics.DidNotReceive().RefreshAsync(Loadout.Load(_main.Db, other).LoadoutId);
    }

    [Fact]
    public async Task Check_TheStoreIsStillWritingTheFiles_ChangesNothing()
    {
        StoreSays(Result("new", "build-2", inProgress: true));

        var outcome = await _reaction.Check(Installation("old", "build-1"), TestContext.Current.CancellationToken);

        outcome.Should().Be(StoreUpdateOutcome.UpdateInProgress);
        _registry.DidNotReceiveWithAnyArgs().RefreshInstallation(default!);
        await _hashes.DidNotReceiveWithAnyArgs().EnsureLocalBaseline(default!, default);
    }

    [Fact]
    public async Task Check_TheBuildIsTheSame_ChangesNothing()
    {
        StoreSays(Result("old", "build-1"));

        var outcome = await _reaction.Check(Installation("old", "build-1"), TestContext.Current.CancellationToken);

        outcome.Should().Be(StoreUpdateOutcome.Unchanged);
        _registry.DidNotReceiveWithAnyArgs().RefreshInstallation(default!);
        await _hashes.DidNotReceiveWithAnyArgs().EnsureLocalBaseline(default!, default);
        await _diagnostics.DidNotReceiveWithAnyArgs().RefreshAsync(default);
    }

    [Fact]
    public async Task Check_TheGameIsGone_ChangesNothing()
    {
        _locator.TryLocate(Arg.Any<IGameData>(), out Arg.Any<GameLocatorResult?>()).Returns(false);

        var outcome = await _reaction.Check(Installation("old", "build-1"), TestContext.Current.CancellationToken);

        outcome.Should().Be(StoreUpdateOutcome.NotFound);
        _registry.DidNotReceiveWithAnyArgs().RefreshInstallation(default!);
    }

    [Fact]
    public async Task Check_TheBaselineBuildFails_StillRefreshesTheRegistry()
    {
        _hashes.EnsureLocalBaseline(Arg.Any<GameInstallation>(), Arg.Any<CancellationToken>())
            .Returns<Task<LocalBaselineResult>>(_ => throw new IOException("disk gone"));
        StoreSays(Result("new", "build-2"));

        var outcome = await _reaction.Check(Installation("old", "build-1"), TestContext.Current.CancellationToken);

        outcome.Should().Be(StoreUpdateOutcome.Refreshed);
        _registry.Received(1).RefreshInstallation(Arg.Any<GameInstallation>());
    }

    [Fact]
    public async Task Check_AGameTheAppNeverManaged_RefreshesTheRegistryAndTouchesNoDiagnostics()
    {
        _registry.TryGetMetadata(Arg.Any<GameInstallation>(), out Arg.Any<GameInstallMetadata.ReadOnly>()).Returns(false);
        StoreSays(Result("new", "build-2"));

        var outcome = await _reaction.Check(Installation("old", "build-1"), TestContext.Current.CancellationToken);

        outcome.Should().Be(StoreUpdateOutcome.Refreshed);
        await _diagnostics.DidNotReceiveWithAnyArgs().RefreshAsync(default);
    }

    private void StoreSays(GameLocatorResult result) =>
        _locator.TryLocate(Arg.Any<IGameData>(), out Arg.Any<GameLocatorResult?>())
            .Returns(call => { call[1] = result; return true; });

    private GameLocatorResult Result(string id, string build, bool inProgress = false)
    {
        var game = Substitute.For<SubstitutableGame>();
        var path = FileSystem.Shared.FromUnsanitizedFullPath(GamePath);
        return new GameLocatorResult
        {
            Game = game,
            Path = path,
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [LocatorId.From(id)],
            Locator = _locator,
            StoreBuildId = build,
            StoreUpdateInProgress = inProgress,
            StoreUpdatePending = inProgress,
        };
    }

    private GameInstallation Installation(string id, string build)
    {
        var result = Result(id, build);
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, result.Path)));
    }
}

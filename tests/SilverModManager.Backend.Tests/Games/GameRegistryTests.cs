using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend.Games;
using SilverModManager.MnemonicDB.Abstractions;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using NSubstitute;

namespace SilverModManager.Backend.Tests.Games;

/// <summary>
/// A store whose files sit on a drive that is gone must cost the user that store's games and nothing else.
/// </summary>
public class GameRegistryTests
{
    [Fact]
    public void LocateGameInstallations_OneLocatorThrows_KeepsTheGamesOfTheOthers()
    {
        var registry = CreateRegistry(new GoodLocator("first"), new ThrowingLocator(), new GoodLocator("last"));

        var installations = registry.LocateGameInstallations();

        installations.Select(i => i.LocatorResult.StoreIdentifier).Should().Equal("first", "last");
    }

    [Fact]
    public void LocateGameInstallations_LocatorThrowsAfterItsFirstResult_KeepsThatResultAndTheOtherLocators()
    {
        var registry = CreateRegistry(new GoodLocator("first", throwAfter: true), new GoodLocator("second"));

        var installations = registry.LocateGameInstallations();

        installations.Select(i => i.LocatorResult.StoreIdentifier).Should().Equal("first", "second");
    }

    [Fact]
    public void LocateGameInstallations_GameWhoseLocationsFailToResolve_KeepsTheOtherGames()
    {
        var registry = CreateRegistry(new GoodLocator("first", brokenFirstGame: true));

        registry.LocateGameInstallations().Select(i => i.LocatorResult.StoreIdentifier).Should().Equal("first-2");
    }

    [Fact]
    public void Problems_ReportedDuringALocate_AreVisibleAfterIt()
    {
        var registry = CreateRegistry(new ReportingLocator("first"), new ReportingLocator("second"));

        registry.Problems.Should().BeEmpty("nothing has been located yet");
        registry.LocateGameInstallations();

        registry.Problems.Select(p => p.Message).Should().Equal("first: problem 1", "second: problem 1");
        registry.Problems.Select(p => p.Locator).Should().Equal("first", "second");
    }

    [Fact]
    public void Problems_AfterClearCache_StayUntilTheNextLocateReplacesThem()
    {
        var registry = CreateRegistry(new ReportingLocator("only"));
        registry.LocateGameInstallations();

        registry.ClearCache();
        registry.Problems.Select(p => p.Message).Should().Equal("only: problem 1");

        registry.LocateGameInstallations();
        registry.Problems.Select(p => p.Message).Should().Equal("only: problem 2");
    }

    [Fact]
    public void Problems_ALocatorThatThrows_IsRecordedAndTheOthersStillRun()
    {
        var registry = CreateRegistry(new ThrowingLocator(), new ReportingLocator("last"));

        registry.LocateGameInstallations();

        registry.Problems.Select(p => p.Message).Should().Contain("last: problem 1");
        registry.Problems.Should().Contain(p => p.Locator == nameof(ThrowingLocator) && p.Message.Contains("No such device"));
    }

    [Fact]
    public void RefreshInstallation_SameGameInTwoFolders_SwapsOnlyTheOneInTheLocatedFolder()
    {
        var locator = new FixedLocator(Located("game", "/a", "old-a"), Located("game", "/b", "old-b"));
        var registry = CreateRegistry(locator);
        var before = registry.LocateGameInstallations();

        var refreshed = Installation(locator, "game", "/b", "new-b");
        registry.RefreshInstallation(refreshed);

        var after = registry.LocateGameInstallations();
        after.Should().HaveCount(2);
        after[0].Should().BeSameAs(before[0]);
        after[1].Should().BeSameAs(refreshed);
    }

    [Fact]
    public void RefreshInstallation_SameGameFromAnotherStore_LeavesThatInstallAlone()
    {
        var locator = new FixedLocator(Located("game", "/a", "steam", GameStore.Steam), Located("game", "/a", "gog", GameStore.GOG));
        var registry = CreateRegistry(locator);
        var before = registry.LocateGameInstallations();

        var refreshed = Installation(locator, "game", "/a", "new-steam", GameStore.Steam);
        registry.RefreshInstallation(refreshed);

        var after = registry.LocateGameInstallations();
        after[0].Should().BeSameAs(refreshed);
        after[1].Should().BeSameAs(before[1]);
    }

    [Fact]
    public void RefreshInstallation_BeforeAnythingIsCached_DoesNotLocateAndDoesNotInventAnEntry()
    {
        var locator = new FixedLocator(Located("game", "/a", "old"));
        var registry = CreateRegistry(locator);

        registry.RefreshInstallation(Installation(locator, "game", "/a", "new"));

        locator.Calls.Should().Be(0, "a refresh has nothing to swap in an empty cache and must not pay for a locate");
        var installations = registry.LocateGameInstallations();
        locator.Calls.Should().Be(1);
        installations.Single().LocatorResult.StoreIdentifier.Should().Be("old");
    }

    [Fact]
    public void RefreshInstallation_AfterClearCache_LeavesTheCacheEmpty()
    {
        var locator = new FixedLocator(Located("game", "/a", "old"));
        var registry = CreateRegistry(locator);
        registry.LocateGameInstallations();
        registry.ClearCache();

        registry.RefreshInstallation(Installation(locator, "game", "/a", "new"));
        registry.LocateGameInstallations();

        locator.Calls.Should().Be(2, "the clear must stand, so the next read locates again");
    }

    [Fact]
    public async Task ClearCache_WhileALocateIsRunning_WaitsForItAndThenClears()
    {
        using var inLocate = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var locator = new FixedLocator(Located("game", "/a", "old")) { OnLocate = () => { inLocate.Set(); release.Wait(); } };
        var registry = CreateRegistry(locator);
        var locating = Task.Run(() => registry.LocateGameInstallations());
        inLocate.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var clearing = Task.Run(registry.ClearCache);
        var finishedWhileLocating = await Task.WhenAny(clearing, Task.Delay(300, TestContext.Current.CancellationToken)) == clearing;
        release.Set();
        await locating;
        await clearing;
        registry.LocateGameInstallations();

        finishedWhileLocating.Should().BeFalse("the clear takes the lock the locate holds");
        locator.Calls.Should().Be(2, "the clear came after the locate, so the list it cached is gone");
    }

    private static Spec Located(string gameId, string path, string identifier, GameStore? store = null) => new(gameId, path, identifier, store);

    private sealed record Spec(string GameId, string Path, string Identifier, GameStore? Store);

    private static GameInstallation Installation(IGameLocator locator, string gameId, string path, string identifier, GameStore? store = null)
    {
        var result = Result(locator, identifier, gameId: gameId, path: path, store: store);
        return new GameInstallation(result, GameLocations.Create(result.Game.GetLocations(result.Path.FileSystem, result)));
    }

    private sealed class FixedLocator(params Spec[] specs) : IGameLocator
    {
        public int Calls { get; private set; }
        public Action? OnLocate { get; init; }

        public IEnumerable<GameLocatorResult> Locate()
        {
            Calls++;
            OnLocate?.Invoke();
            return specs.Select(spec => Result(this, spec.Identifier, gameId: spec.GameId, path: spec.Path, store: spec.Store)).ToList();
        }
    }

    private static GameRegistry CreateRegistry(params IGameLocator[] locators)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(Substitute.For<IConnection>());
        foreach (var locator in locators) services.AddSingleton(locator);

        return new GameRegistry(services.BuildServiceProvider());
    }

    private static GameLocatorResult Result(IGameLocator locator, string storeIdentifier, bool brokenLocations = false, string gameId = "game", string path = "/game", GameStore? store = null)
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var game = new FakeGame(GameId.From(gameId), brokenLocations);

        return new GameLocatorResult
        {
            Game = game,
            Path = fileSystem.FromUnsanitizedFullPath(path),
            Store = store ?? GameStore.Steam,
            StoreIdentifier = storeIdentifier,
            LocatorIds = [LocatorId.From(storeIdentifier)],
            Locator = locator,
        };
    }

    /// <summary>
    /// A mock cannot stand in: GameId refuses its default value, which is what an unconfigured mock hands back.
    /// </summary>
    private sealed class FakeGame(GameId gameId, bool brokenLocations) : IGameData
    {
        public GameId GameId => gameId;
        public StoreIdentifiers StoreIdentifiers => new(gameId);
        public string DisplayName => "Fake game";
        public Optional<NexusModsGameId> NexusModsGameId => Optional<NexusModsGameId>.None;
        public IStreamFactory IconImage => throw new NotSupportedException();
        public IStreamFactory TileImage => throw new NotSupportedException();
        public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();

        public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
            => brokenLocations ? throw new IOException("No such device") : ImmutableDictionary<LocationId, AbsolutePath>.Empty;
    }

    private sealed class GoodLocator(string name, bool throwAfter = false, bool brokenFirstGame = false) : IGameLocator
    {
        public IEnumerable<GameLocatorResult> Locate()
        {
            if (brokenFirstGame)
            {
                yield return Result(this, name, brokenLocations: true);
                yield return Result(this, name + "-2");
                yield break;
            }

            yield return Result(this, name);
            if (throwAfter) throw new IOException("No such device");
        }
    }

    private sealed class ThrowingLocator : IGameLocator
    {
        public IEnumerable<GameLocatorResult> Locate() => throw new IOException("No such device");
    }

    private sealed class ReportingLocator(string name) : IGameLocator
    {
        private int _calls;

        public IEnumerable<GameLocatorResult> Locate() => [];

        public IEnumerable<GameLocatorResult> Locate(IGameLocatorProblemSink problems)
        {
            problems.Report(name, $"{name}: problem {++_calls}");
            return [];
        }
    }
}

using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend.Games;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Backend.Tests.Games;

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

    private static GameRegistry CreateRegistry(params IGameLocator[] locators)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(Substitute.For<IConnection>());
        foreach (var locator in locators) services.AddSingleton(locator);

        return new GameRegistry(services.BuildServiceProvider());
    }

    private static GameLocatorResult Result(IGameLocator locator, string storeIdentifier, bool brokenLocations = false)
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var game = Substitute.For<IGameData>();
        if (brokenLocations) game.GetLocations(Arg.Any<IFileSystem>(), Arg.Any<GameLocatorResult>()).Returns(_ => throw new IOException("No such device"));
        else game.GetLocations(Arg.Any<IFileSystem>(), Arg.Any<GameLocatorResult>()).Returns(ImmutableDictionary<LocationId, AbsolutePath>.Empty);

        return new GameLocatorResult
        {
            Game = game,
            Path = fileSystem.FromUnsanitizedFullPath("/game"),
            Store = GameStore.Steam,
            StoreIdentifier = storeIdentifier,
            LocatorIds = [LocatorId.From(storeIdentifier)],
            Locator = locator,
        };
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

using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Backend.Games.Locators;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Backend.Tests.Games;

public class StoreIdMapTests
{
    private static IGameData Game(string name, params long[] gogIds)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns(name);
        game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(1)) { GOGProductIds = [..gogIds] });
        return game;
    }

    [Fact]
    public void Build_DuplicateId_KeepsTheFirstGameAndRecordsBoth()
    {
        var first = Game("First", 7);
        var second = Game("Second", 7, 8);
        var logger = new RecordingLogger<StoreIdMapTests>();

        var map = StoreIdMap.Build([first, second], game => game.StoreIdentifiers.GOGProductIds, "GOG", "TestLocator", logger);

        map.Games[7].Should().BeSameAs(first);
        map.Games[8].Should().BeSameAs(second);
        var problem = map.Problems.Should().ContainSingle().Subject;
        problem.Locator.Should().Be("TestLocator");
        problem.Message.Should().Contain("First").And.Contain("Second").And.Contain("GOG").And.Contain("7");
        logger.Count(LogLevel.Error).Should().Be(1);
    }

    [Fact]
    public void Build_SameGameListingAnIdTwice_IsNotAProblem()
    {
        var game = Game("Only", 7, 7);

        var map = StoreIdMap.Build([game], g => g.StoreIdentifiers.GOGProductIds, "GOG", "TestLocator", new RecordingLogger<StoreIdMapTests>());

        map.Games.Should().ContainSingle();
        map.Problems.Should().BeEmpty();
    }
}

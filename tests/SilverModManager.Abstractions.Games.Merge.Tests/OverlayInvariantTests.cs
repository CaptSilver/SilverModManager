using FluentAssertions;
using NSubstitute;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class OverlayInvariantTests
{
    private static IGameData Game(string name, bool online, bool overlay)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns(name);
        game.HasOnlinePlay.Returns(online);
        game.DeploysThroughOverlay.Returns(overlay);
        return game;
    }

    private static MergeEngines EngineServing(params IGameData[] games)
    {
        var engine = Substitute.For<IMergeEngine>();
        engine.Serves(Arg.Any<IGameData>()).Returns(call => games.Contains(call.Arg<IGameData>()));
        return new MergeEngines([engine]);
    }

    [Fact]
    public void AMergingGameWithOnlinePlayAndNoOverlayBreaksTheRuleAndIsNamed()
    {
        var game = Game("Online Merger", online: true, overlay: false);

        var act = () => OverlayInvariant.Check([game], EngineServing(game));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Online Merger*overlay*");
    }

    [Fact]
    public void EveryBreakingGameIsNamed()
    {
        var first = Game("First", online: true, overlay: false);
        var second = Game("Second", online: true, overlay: false);

        var act = () => OverlayInvariant.Check([first, second], EngineServing(first, second));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("First").And.Contain("Second");
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void OnlyTheMergingOnlineGameWithoutOverlayBreaksTheRule(bool online, bool overlay, bool merges)
    {
        var game = Game("Test Game", online, overlay);

        var violations = OverlayInvariant.Violations([game], EngineServing(merges ? [game] : []));

        violations.Should().BeEmpty();
    }

    [Fact]
    public void FlippingTheOverlayFactOfAnOtherwiseValidGameBreaksTheRule()
    {
        var game = Game("Flipped", online: true, overlay: true);
        var engines = EngineServing(game);
        OverlayInvariant.Violations([game], engines).Should().BeEmpty();

        game.DeploysThroughOverlay.Returns(false);

        OverlayInvariant.Violations([game], engines).Should().ContainSingle().Which.Should().BeSameAs(game);
    }
}

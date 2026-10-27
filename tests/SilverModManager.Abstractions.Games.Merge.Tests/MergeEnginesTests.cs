using FluentAssertions;
using NSubstitute;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class MergeEnginesTests
{
    private static IMergeEngine Engine(bool serves)
    {
        var engine = Substitute.For<IMergeEngine>();
        engine.Serves(Arg.Any<IGameData>()).Returns(serves);
        return engine;
    }

    private static readonly IGameData Game = Substitute.For<IGameData>();

    [Fact]
    public void TheEngineThatServesTheGameIsPicked()
    {
        var mine = Engine(true);
        new MergeEngines([Engine(false), mine]).For(Game).Should().BeSameAs(mine);
    }

    [Fact]
    public void NoEngineIsNull() => new MergeEngines([Engine(false)]).For(Game).Should().BeNull();

    [Fact]
    public void TwoEnginesForOneGameIsAConfigurationError()
        => new Action(() => new MergeEngines([Engine(true), Engine(true)]).For(Game)).Should().Throw<InvalidOperationException>();
}

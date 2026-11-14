using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Games.Starters;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Tests.Starters;

public class GameStarterSelectorTests
{
    private static IGameStarter StarterOf(GameStarterKind kind, bool canCarry = true)
    {
        var starter = Substitute.For<IGameStarter>();
        starter.Kind.Returns(kind);
        starter.CanCarryArguments(Arg.Any<GameStartRequest>()).Returns(canCarry);
        return starter;
    }

    private static GameStartRequest RequestFor(StoreClient? client, bool isPrimaryExe = true, string[]? callerArgs = null, string[]? extraArgs = null, bool replacesCommand = false)
        => StarterRequests.For(client, null, isPrimaryExe, callerArgs, extraArgs, replacesCommand);

    private static GameStarterSelector SelectorOf(params IGameStarter[] starters) => new(starters);

    private static readonly StoreClient SteamClient = new(StoreClientKind.Steam, "42", false);
    private static readonly StoreClient HeroicClient = new(StoreClientKind.Heroic, "fortnite", false);
    private static readonly StoreClient LutrisClient = new(StoreClientKind.Lutris, "some-game", false);

    [Fact]
    public void For_ReplacedCommand_PicksProcess()
    {
        var process = StarterOf(GameStarterKind.Process);
        var steam = StarterOf(GameStarterKind.Steam);

        var choice = SelectorOf(process, StarterOf(GameStarterKind.Direct)).For(RequestFor(SteamClient, replacesCommand: true), steam);

        choice.Starter.Should().BeSameAs(process);
        choice.FallbackNotice.Should().BeNull();
    }

    [Fact]
    public void For_SteamPrimaryExe_PicksTheToolsSteamStarter()
    {
        var steam = StarterOf(GameStarterKind.Steam);

        var choice = SelectorOf(StarterOf(GameStarterKind.Direct)).For(RequestFor(SteamClient), steam);

        choice.Starter.Should().BeSameAs(steam);
        choice.FallbackNotice.Should().BeNull();
    }

    [Fact]
    public void For_SteamWithAScriptExtenderSpec_PicksDirect()
    {
        var direct = StarterOf(GameStarterKind.Direct);

        var choice = SelectorOf(direct).For(RequestFor(SteamClient, isPrimaryExe: false), StarterOf(GameStarterKind.Steam));

        choice.Starter.Should().BeSameAs(direct);
        choice.FallbackNotice.Should().BeNull();
    }

    [Fact]
    public void For_HeroicPrimaryExe_PicksHeroic()
    {
        var heroic = StarterOf(GameStarterKind.Heroic);

        var choice = SelectorOf(heroic, StarterOf(GameStarterKind.Direct)).For(RequestFor(HeroicClient), StarterOf(GameStarterKind.Steam));

        choice.Starter.Should().BeSameAs(heroic);
        choice.FallbackNotice.Should().BeNull();
    }

    [Fact]
    public void For_HeroicThatCannotCarryTheArguments_PicksDirectAndSaysWhatIsLost()
    {
        var direct = StarterOf(GameStarterKind.Direct);

        var choice = SelectorOf(StarterOf(GameStarterKind.Heroic, canCarry: false), direct)
            .For(RequestFor(HeroicClient, extraArgs: ["-modded"]), StarterOf(GameStarterKind.Steam));

        choice.Starter.Should().BeSameAs(direct);
        choice.FallbackNotice.Should().Contain("-modded").And.Contain("cloud saves").And.Contain("Heroic");
    }

    [Fact]
    public void For_LutrisWithArguments_PicksDirect()
    {
        var direct = StarterOf(GameStarterKind.Direct);

        var choice = SelectorOf(StarterOf(GameStarterKind.Lutris, canCarry: false), direct)
            .For(RequestFor(LutrisClient, callerArgs: ["-windowed"]), StarterOf(GameStarterKind.Steam));

        choice.Starter.Should().BeSameAs(direct);
        choice.FallbackNotice.Should().Contain("-windowed").And.Contain("Lutris");
    }

    [Fact]
    public void For_AStoreWithoutAStoreClient_PicksDirectWithNoNotice()
    {
        var direct = StarterOf(GameStarterKind.Direct);

        var choice = SelectorOf(direct).For(RequestFor(null), StarterOf(GameStarterKind.Steam));

        choice.Starter.Should().BeSameAs(direct);
        choice.FallbackNotice.Should().BeNull();
    }

    [Fact]
    public void For_AClientKindWithNoStarter_PicksDirectAndSaysSo()
    {
        var direct = StarterOf(GameStarterKind.Direct);

        var choice = SelectorOf(direct).For(RequestFor(HeroicClient), StarterOf(GameStarterKind.Steam));

        choice.Starter.Should().BeSameAs(direct);
        choice.FallbackNotice.Should().Contain("Heroic launches are not available in this build");
    }

    [Fact]
    public void TwoStartersOfOneKind_AreRefused()
    {
        var act = () => SelectorOf(StarterOf(GameStarterKind.Direct), StarterOf(GameStarterKind.Direct));

        act.Should().Throw<ArgumentException>();
    }
}

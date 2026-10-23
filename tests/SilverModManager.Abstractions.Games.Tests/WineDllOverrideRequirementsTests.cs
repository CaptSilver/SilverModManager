using FluentAssertions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NSubstitute;

namespace NexusMods.Abstractions.Games.Tests;

public class WineDllOverrideRequirementsTests
{
    [Fact]
    public void ForAGameThatDeclaresNothingIsEmpty()
    {
        var game = Substitute.For<IGameData>();

        WineDllOverrideRequirements.For(game, default).Should().BeEmpty();
    }

    [Fact]
    public void ForAGameThatDeclaresOverridesReturnsThem()
    {
        var game = Substitute.For<IGameData, IWineDllOverrideRequirements>();
        WineDllOverride[] declared = [new("DWrite", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])];
        ((IWineDllOverrideRequirements)game).RequiredWineDllOverrides(default).Returns(declared);

        WineDllOverrideRequirements.For(game, default).Should().Equal(declared);
    }
}

public class LauncherSkipTests
{
    [Fact]
    public void ForAGameWithoutALauncherIsEmpty()
    {
        GameLauncherSkip.For(Substitute.For<IGameData>()).Should().BeEmpty();
    }

    [Fact]
    public void ForAGameThatDeclaresArgumentsReturnsThem()
    {
        var game = Substitute.For<IGameData, IGameLauncherSkip>();
        ((IGameLauncherSkip)game).LauncherSkipArguments.Returns(["--skip"]);

        GameLauncherSkip.For(game).Should().Equal("--skip");
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics.Emitters;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class MergeHookupsTests
{
    [Fact]
    public void AMergeGameWithoutTheEngineEmitterFailsTheCheckNamingTheGameAndTheEmitter()
    {
        var game = Substitute.For<IGame, IHasMergeGame>();
        game.DisplayName.Returns("Test Game");
        game.DiagnosticEmitters.Returns([]);

        var act = () => AMergeGame.CheckHookups(game);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Test Game*EngineMergeDiagnosticsEmitter*DiagnosticEmitters*");
    }

    [Fact]
    public void AMergeGameWithTheEngineEmitterPassesTheCheck()
    {
        var game = Substitute.For<IGame, IHasMergeGame>();
        game.DiagnosticEmitters.Returns([new EngineMergeDiagnosticsEmitter(new MergeEngines([]), NullLogger<EngineMergeDiagnosticsEmitter>.Instance)]);

        var act = () => AMergeGame.CheckHookups(game);

        act.Should().NotThrow();
    }

    [Fact]
    public void AGameTheEngineDoesNotServeHasNothingToCheck()
    {
        var game = Substitute.For<IGame>();
        game.DiagnosticEmitters.Returns(Array.Empty<IDiagnosticEmitter>());

        var act = () => AMergeGame.CheckHookups(game);

        act.Should().NotThrow();
    }
}

using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Tests;

public class ModLoaderPathsTests
{
    public interface INativeCodeGame : IGame, IGameNativeCode;

    private static GamePath P(string path) => new(LocationId.Game, path);

    private static (IGame Game, GameInstallation Installation) Make(string primary = "Game.exe", Func<GamePath, bool>? hooks = null)
    {
        var game = Substitute.For<INativeCodeGame>();
        game.HooksGameCode(Arg.Any<GamePath>()).Returns(call => hooks?.Invoke(call.Arg<GamePath>()) ?? false);
        game.GetPrimaryFile(Arg.Any<GameInstallation>()).Returns(P(primary));
        return (game, default!);
    }

    private static bool Looks(string path, string primary = "Game.exe", Func<GamePath, bool>? hooks = null)
    {
        var (game, installation) = Make(primary, hooks);
        return ModLoaderPaths.LooksLikeAMod(game, installation, P(path));
    }

    [Fact] public void ProxyDll_InTheExeFolder_IsAMod() => Looks("bin/x64/winmm.dll", "bin/x64/Game.exe").Should().BeTrue();
    [Fact] public void ProxyDll_InTheRoot_IsAMod() => Looks("version.dll").Should().BeTrue();
    [Fact] public void ProxyDll_Elsewhere_IsNot() => Looks("Data/dinput8.dll").Should().BeFalse();
    [Fact] public void ProxyDll_MatchesAnyCase() => Looks("DINPUT8.dll").Should().BeTrue();
    [Fact] public void TopLevelSrc_IsAMod() => Looks("src/main.cpp").Should().BeTrue();
    [Fact] public void NestedSrc_IsNot() => Looks("Data/src/main.cpp").Should().BeFalse();
    [Fact] public void HooksGameCode_IsConsulted() => Looks("Data/SKSE/Plugins/x.dll", hooks: p => p.Path.ToString().EndsWith("x.dll")).Should().BeTrue();
    [Fact] public void GameExe_IsNot() => Looks("Game.exe").Should().BeFalse();

    [Fact]
    public void GameWithoutNativeCode_StillGetsTheProxyAndSrcRules()
    {
        var game = Substitute.For<IGame>();
        game.GetPrimaryFile(Arg.Any<GameInstallation>()).Returns(P("Game.exe"));

        ModLoaderPaths.LooksLikeAMod(game, default!, P("dxgi.dll")).Should().BeTrue();
        ModLoaderPaths.LooksLikeAMod(game, default!, P("Data/a.dll")).Should().BeFalse();
    }
}

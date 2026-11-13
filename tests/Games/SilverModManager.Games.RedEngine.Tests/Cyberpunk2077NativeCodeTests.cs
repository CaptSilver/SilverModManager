using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.RedEngine.Tests;

/// <summary>Which Cyberpunk 2077 files hook the game, and which of those are loader runtime or plugins, resolved from the real service graph.</summary>
public class Cyberpunk2077NativeCodeTests(IServiceProvider serviceProvider)
{
    [Theory]
    [InlineData("red4ext/RED4ext.dll", true)]
    [InlineData("red4ext/plugins/ArchiveXL/ArchiveXL.dll", true)]
    [InlineData("bin/x64/plugins/cet.asi", true)]
    [InlineData("bin/x64/winmm.dll", true)]
    [InlineData("bin/x64/version.dll", true)]
    [InlineData("bin/x64/other.dll", false)]
    [InlineData("bin/x64/plugins/cet.dll", false)]
    [InlineData("bin/x64/plugins/sub/cet.asi", false)]
    [InlineData("red4ext/sub/x.dll", false)]
    [InlineData("red4ext/config.ini", false)]
    [InlineData("bin/x64/Cyberpunk2077.exe", false)]
    public void HooksGameCode_NamesRed4extCetAndTheirPlugins(string path, bool hooks)
    {
        var game = serviceProvider.GetServices<IGame>().OfType<Cyberpunk2077.Cyberpunk2077Game>().Single();

        ((IGameNativeCode)game).HooksGameCode(new GamePath(LocationId.Game, path)).Should().Be(hooks);
    }

    [Theory]
    [InlineData("red4ext/RED4ext.dll", NativeCodeRole.LoaderRuntime)]
    [InlineData("bin/x64/version.dll", NativeCodeRole.LoaderRuntime)]
    [InlineData("bin/x64/winmm.dll", NativeCodeRole.LoaderRuntime)]
    [InlineData("bin/x64/plugins/cet.asi", NativeCodeRole.LoaderPlugin)]
    [InlineData("red4ext/plugins/ArchiveXL/ArchiveXL.dll", NativeCodeRole.LoaderPlugin)]
    [InlineData("bin/x64/other.dll", null)]
    public void RoleOf_SplitsTheRuntimeFromItsPlugins(string path, NativeCodeRole? role)
    {
        var game = serviceProvider.GetServices<IGame>().OfType<Cyberpunk2077.Cyberpunk2077Game>().Single();

        ((IGameNativeCode)game).RoleOf(new GamePath(LocationId.Game, path)).Should().Be(role);
    }

    [Theory]
    [InlineData("red4ext/RED4ext.dll")]
    [InlineData("bin/x64/version.dll")]
    [InlineData("bin/x64/winmm.dll")]
    [InlineData("bin/x64/plugins/cet.asi")]
    [InlineData("red4ext/plugins/ArchiveXL/ArchiveXL.dll")]
    public void EveryHookedPathHasARole(string path)
    {
        var game = (IGameNativeCode)serviceProvider.GetServices<IGame>().OfType<Cyberpunk2077.Cyberpunk2077Game>().Single();
        var gamePath = new GamePath(LocationId.Game, path);

        game.HooksGameCode(gamePath).Should().BeTrue();
        game.RoleOf(gamePath).Should().NotBeNull();
    }
}

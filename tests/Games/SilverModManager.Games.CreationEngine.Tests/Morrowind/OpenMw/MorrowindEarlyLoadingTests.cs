using FluentAssertions;
using NexusMods.Paths;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Morrowind.OpenMw;

/// <summary>
/// With OpenMW, <c>builtin.omwscripts</c> loads before anything the app orders. A config the user's openmw.cfg
/// chains to with <c>config=</c> loads after it, so its content is not early, and neither are the user's own lines.
/// </summary>
public class MorrowindEarlyLoadingTests(ITestOutputHelper helper) : AOpenMwTest<MorrowindEarlyLoadingTests>(helper)
{
    protected override int[] RootsWithConfig => [0];

    protected override string ConfigText => "config=chain\n" + ConfigFixture + "content=Own.esp\n";

    [Fact]
    public async Task OnlyTheBuiltInScriptsLoadEarly()
    {
        var directory = Roots.Candidates[0] / "chain";
        directory.CreateDirectory();
        await (directory / "openmw.cfg").WriteAllTextAsync("content=Later.esm\ncontent=LaterAddon.omwaddon\n");

        Game.EarlyLoadingPlugins(GameInstallation).Should().Equal("builtin.omwscripts");
    }
}

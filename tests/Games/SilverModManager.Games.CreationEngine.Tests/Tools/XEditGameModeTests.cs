using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.Tools;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Tools;

/// <summary>
/// xEdit picks its game from the exe name, and a <c>-sse</c>-style switch on the command line overrides that. The
/// switch is only added when the exe name would not already choose the game, so a renamed <c>SSEEdit.exe</c> gets
/// no argument and a generic <c>xEdit64.exe</c> does. The switch names are the ones xEdit's own source reads.
/// </summary>
public class XEditGameModeTests(IServiceProvider provider)
{
    private static readonly Dictionary<GameId, string> Switches = new()
    {
        [SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId] = "-sse",
        [SilverModManager.Games.CreationEngine.SkyrimLE.SkyrimLE.GameId] = "-tes5",
        [SilverModManager.Games.CreationEngine.SkyrimVR.SkyrimVR.GameId] = "-tes5vr",
        [SilverModManager.Games.CreationEngine.Enderal.Enderal.GameId] = "-enderal",
        [SilverModManager.Games.CreationEngine.Fallout4.Fallout4.GameId] = "-fo4",
        [SilverModManager.Games.CreationEngine.Fallout4VR.Fallout4VR.GameId] = "-fo4vr",
        [SilverModManager.Games.CreationEngine.Fallout3.Fallout3.GameId] = "-fo3",
        [SilverModManager.Games.CreationEngine.FalloutNewVegas.FalloutNewVegas.GameId] = "-fnv",
        [SilverModManager.Games.CreationEngine.Oblivion.Oblivion.GameId] = "-tes4",
    };

    public static TheoryData<string> KnownGames => new(Switches.Keys.Select(id => id.ToString()));

    private IGame GameOf(string id) => provider.GetServices<IGame>().Single(g => g.GameId.ToString() == id);

    [Theory]
    [MemberData(nameof(KnownGames))]
    public void GenericExe_GetsTheGamesSwitch(string id)
    {
        XEditGameMode.Arguments(GameOf(id), "xEdit64.exe").Should().Equal(Switches[GameId.From(id)]);
    }

    [Theory]
    [InlineData("Skyrim Special Edition", "SSEEdit64.exe")]
    [InlineData("Skyrim Special Edition", "sseedit.exe")]
    [InlineData("Fallout 4", "FO4Edit64.exe")]
    [InlineData("Fallout 4 VR", "FO4VREdit.exe")]
    [InlineData("Skyrim VR", "TES5VREdit.exe")]
    [InlineData("Fallout: New Vegas", "FNVEdit64.exe")]
    public void GameSpecificExe_NeedsNoSwitch(string displayName, string exe)
    {
        var game = provider.GetServices<IGame>().Single(g => g.DisplayName == displayName);

        XEditGameMode.Arguments(game, exe).Should().BeEmpty();
    }

    [Fact]
    public void ExeNamedForAnotherGame_IsOverriddenByTheSwitch()
    {
        // Fallout 4 run through a copy named SSEEdit: xEdit would open Skyrim SE without the switch.
        XEditGameMode.Arguments(GameOf(SilverModManager.Games.CreationEngine.Fallout4.Fallout4.GameId.ToString()), "SSEEdit64.exe")
            .Should().Equal("-fo4");
    }

    [Fact]
    public void VrExeName_IsNotTakenForThePlainGame()
    {
        // 'tes5vr' is tried before 'tes5' in xEdit, and so is here: a TES5VREdit copy for plain Skyrim needs -tes5.
        XEditGameMode.Arguments(GameOf(SilverModManager.Games.CreationEngine.SkyrimLE.SkyrimLE.GameId.ToString()), "TES5VREdit.exe")
            .Should().Equal("-tes5");
    }

    [Theory]
    [InlineData("CreationEngine.Starfield")]
    [InlineData("CreationEngine.Morrowind")]
    [InlineData("CreationEngine.EnderalSE")]
    public void GameWithoutAVerifiedSwitch_GetsNone(string id)
    {
        XEditGameMode.Arguments(GameOf(id), "xEdit64.exe").Should().BeEmpty();
    }
}

using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tools;

/// <summary>
/// Which game xEdit should open. xEdit takes it from a switch on the command line, and failing that from the
/// exe's file name (<c>SSEEdit.exe</c>, <c>FO4Edit64.exe</c>); a generic <c>xEdit64.exe</c> defaults to Fallout 4.
/// The xEdit, xLODGen, TexGen and DynDOLOD definitions share this one rule.
/// </summary>
/// <remarks>
/// The mode names and their order are those of <c>GameModes</c> in xEdit's <c>wbInit.pas</c> (TES5Edit repository,
/// dev branch). There is no switch for Morrowind, Starfield, Enderal SE or Oblivion Remastered in that list, so
/// those games get none; their tools carry a game-specific exe name instead.
/// </remarks>
public static class XEditGameMode
{
    // Order matters: xEdit takes the first mode found in the exe name, so 'tes5vr' must come before 'tes5'.
    private static readonly string[] Modes = ["tes5vr", "fo4vr", "tes4", "tes5", "enderal", "sse", "fo3", "fnv", "fo4", "fo76"];

    private static readonly Dictionary<GameId, string> ModeOfGame = new()
    {
        [SkyrimSE.SkyrimSE.GameId] = "sse",
        [SkyrimLE.SkyrimLE.GameId] = "tes5",
        [SkyrimVR.SkyrimVR.GameId] = "tes5vr",
        [Enderal.Enderal.GameId] = "enderal",
        [Fallout4.Fallout4.GameId] = "fo4",
        [Fallout4VR.Fallout4VR.GameId] = "fo4vr",
        [Fallout3.Fallout3.GameId] = "fo3",
        [FalloutNewVegas.FalloutNewVegas.GameId] = "fnv",
        [Oblivion.Oblivion.GameId] = "tes4",
    };

    /// <summary>
    /// The switch to add for <paramref name="game"/> run from an exe called <paramref name="exeFileName"/>: none when
    /// the name already selects that game, or when xEdit has no switch for the game.
    /// </summary>
    public static IReadOnlyList<string> Arguments(IGameData game, string exeFileName)
    {
        if (ModeOfGame.TryGetValue(game.GameId, out var mode) is false) return [];
        return ModeInName(exeFileName) == mode ? [] : [$"-{mode}"];
    }

    private static string? ModeInName(string exeFileName)
    {
        var name = exeFileName.ToLowerInvariant();
        return Modes.FirstOrDefault(name.Contains);
    }
}

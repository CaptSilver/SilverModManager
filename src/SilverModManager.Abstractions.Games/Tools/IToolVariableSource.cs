using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>
/// Implemented by a game that has paths a tool's arguments can name. The argument resolver reads it from the
/// loadout's game, so it never has to know which game family it is looking at.
/// </summary>
public interface IToolVariableSource
{
    /// <summary>Variable name (see <see cref="ToolVariableNames"/>) to the game path it stands for.</summary>
    IReadOnlyDictionary<string, GamePath> ToolVariables(Loadout.ReadOnly loadout);
}

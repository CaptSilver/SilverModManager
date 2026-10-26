using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// Implemented by a game that needs components installed in its Wine prefix, such as the Visual C++ runtime.
/// </summary>
/// <remarks>
/// The prefix diagnostic and the prefix setup both read this one declaration, so the list lives on the game
/// and nowhere else. It is synchronous because every known answer is a database read of the loadout. Whether
/// a component is already in the prefix is not the game's business: that is what <see cref="IPrefixInspector"/> answers.
/// </remarks>
public interface IWinePrefixRequirements
{
    /// <summary>
    /// The components the loadout needs in the prefix. Empty when it needs none.
    /// </summary>
    IReadOnlyList<PrefixComponentId> RequiredPrefixComponents(Loadout.ReadOnly loadout);
}

public static class WinePrefixRequirements
{
    /// <summary>
    /// The components <paramref name="game"/> requires for <paramref name="loadout"/>, or none when the game
    /// does not declare any.
    /// </summary>
    /// <remarks>
    /// Callers hold an <see cref="IGameData"/>; the registered game instance behind it is what implements
    /// <see cref="IWinePrefixRequirements"/>.
    /// </remarks>
    public static IReadOnlyList<PrefixComponentId> For(IGameData game, Loadout.ReadOnly loadout)
    {
        return game is IWinePrefixRequirements requirements ? requirements.RequiredPrefixComponents(loadout) : [];
    }
}

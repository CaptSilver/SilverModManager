using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// Implemented by a game whose mods only load under Wine when certain DLLs are overridden.
/// </summary>
/// <remarks>
/// The diagnostics, the Steam launch option and the launch itself all read this one declaration, so the
/// list lives on the game and nowhere else. It is synchronous because every known answer is a database
/// read of the loadout.
/// </remarks>
public interface IWineDllOverrideRequirements
{
    /// <summary>
    /// The overrides the loadout needs. Empty when it needs none.
    /// </summary>
    IReadOnlyList<WineDllOverride> RequiredWineDllOverrides(Loadout.ReadOnly loadout);
}

public static class WineDllOverrideRequirements
{
    /// <summary>
    /// The overrides <paramref name="game"/> requires for <paramref name="loadout"/>, or none when the game
    /// does not declare any.
    /// </summary>
    /// <remarks>
    /// Callers hold an <see cref="IGameData"/>; the registered game instance behind it is what implements
    /// <see cref="IWineDllOverrideRequirements"/>.
    /// </remarks>
    public static IReadOnlyList<WineDllOverride> For(IGameData game, Loadout.ReadOnly loadout)
    {
        return game is IWineDllOverrideRequirements requirements ? requirements.RequiredWineDllOverrides(loadout) : [];
    }
}

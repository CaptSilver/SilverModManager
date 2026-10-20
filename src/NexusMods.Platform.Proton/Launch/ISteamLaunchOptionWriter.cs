using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// Composes the Steam launch options line for a loadout and stores it in Steam's config. The
/// <c>steam-launch-option</c> verb and the GUI's dialog both go through it, so they refuse the same cases with the
/// same texts.
/// </summary>
public interface ISteamLaunchOptionWriter
{
    /// <summary>
    /// The launcher span for the loadout, ready to paste into a game's launch options.
    /// </summary>
    string ComposeLine(string loadoutName);

    /// <summary>
    /// Set when the launcher script the line points at does not exist yet, so a pasted line would not start anything.
    /// </summary>
    string? MissingShimWarning { get; }

    /// <summary>
    /// Stores the launcher span in the game's launch options. Refuses, without touching Steam's files, while Steam
    /// runs, for games that are not Steam games, when the launcher script is missing, and when the config cannot be
    /// edited safely.
    /// </summary>
    LocalConfigVdfWriter.Result Write(GameInstallation installation, string loadoutName);
}

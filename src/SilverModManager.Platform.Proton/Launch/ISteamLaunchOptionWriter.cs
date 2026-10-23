using NexusMods.Paths;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// Composes the Steam launch options line for a loadout and stores it in Steam's config. The
/// <c>steam-launch-option</c> verb and the GUI's dialog both go through it, so they refuse the same cases with the
/// same texts.
/// </summary>
public interface ISteamLaunchOptionWriter
{
    /// <summary>
    /// The launcher span for the loadout, ready to paste into a game's launch options. It starts with the DLL
    /// overrides the game needs for the loadout, when it needs any.
    /// </summary>
    string ComposeLine(Loadout.ReadOnly loadout);

    /// <summary>
    /// Set when the launcher script the line points at does not exist yet, so a pasted line would not start anything.
    /// </summary>
    string? MissingShimWarning { get; }

    /// <summary>
    /// Stores the launcher span in the game's launch options. Refuses, without touching Steam's files, while Steam
    /// runs, for games that are not Steam games, when the launcher script is missing, and when the config cannot be
    /// edited safely.
    /// </summary>
    LocalConfigVdfWriter.Result Write(GameInstallation installation, Loadout.ReadOnly loadout);

    /// <summary>
    /// Swaps another manager's launch line for the launcher span when the stored line is still
    /// <paramref name="confirmedOldLine"/>, the one the user was shown. The old line is kept in a record file in
    /// the data home. Refuses for the same cases as <see cref="Write"/>, and when the stored line changed.
    /// </summary>
    LocalConfigVdfWriter.Result Replace(GameInstallation installation, Loadout.ReadOnly loadout, string confirmedOldLine);

    /// <summary>
    /// The file <see cref="Replace"/> appends the replaced line to, or null for a game that is not a Steam game.
    /// </summary>
    AbsolutePath? ReplacedLineRecord(GameInstallation installation);
}

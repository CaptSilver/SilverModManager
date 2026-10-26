using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>
/// Implemented by a game whose INI files each loadout keeps its own copy of. <see cref="GameInis"/> does the work
/// from what a game says here.
/// </summary>
public interface IGameIniFiles
{
    /// <summary>The INI files a loadout keeps its own copy of.</summary>
    IniSet IniFiles { get; }

    /// <summary>Where the INI called <paramref name="fileName"/> (one of <see cref="IniFiles"/>) lives.</summary>
    GamePath IniPath(string fileName);

    /// <summary>
    /// The file that holds the keys the app owns, or null when the game has none. They go in one file rather than
    /// every file, because a later file would override an earlier one.
    /// </summary>
    GamePath? ManagedKeysFile { get; }

    /// <summary>
    /// The keys the app owns in <see cref="ManagedKeysFile"/> for this loadout. Must depend on the loadout's
    /// database state alone: the same bytes are rendered when the bake is recorded and when it is written.
    /// </summary>
    IReadOnlyList<IniKeyEdit> ManagedKeys(Loadout.ReadOnly loadout);
}

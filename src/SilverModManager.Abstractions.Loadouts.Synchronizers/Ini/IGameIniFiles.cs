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

    /// <summary>
    /// Fills in the part of <paramref name="path"/> that is rendered from the loadout rather than kept as a
    /// setting (Morrowind's plugin list), after the tweaks and <see cref="ManagedKeys"/> are in. Like
    /// <see cref="ManagedKeys"/> it must depend on the loadout's database state alone.
    /// </summary>
    Task RenderDerivedKeys(IniDocument ini, GamePath path, Loadout.ReadOnly loadout) => Task.CompletedTask;

    /// <summary>
    /// Whether a key that <see cref="RenderDerivedKeys"/> rewrites is <paramref name="key"/>. A change the game
    /// makes to such a key is reset like one to a managed key, not adopted into the baseline.
    /// </summary>
    bool IsDerivedKey(GamePath path, string section, string key) => false;
}

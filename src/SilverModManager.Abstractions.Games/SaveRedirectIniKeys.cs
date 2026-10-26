using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// The INI keys a game's save redirect asks for.
/// </summary>
public static class SaveRedirectIniKeys
{
    /// <summary>
    /// The key the loadout's saves need in the game's INI: empty unless the loadout keeps its saves apart, the game
    /// plans a redirect through an INI key, and the plan is one the INI can carry. A loader key or an unsupported plan
    /// is for the caller that shows the toggle, not for the INI.
    /// </summary>
    public static IEnumerable<IniKeyEdit> For(object game, Loadout.ReadOnly loadout)
    {
        if (!Loadout.KeepSavesWithLoadout.Get(loadout) || game is not IPerLoadoutSaves saves) yield break;
        if (saves.PlanSaveRedirect(loadout) is SaveRedirectPlan.IniKey key)
            yield return new IniKeyEdit(key.Section, key.Key, key.Value);
    }
}

using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Emitters;

/// <summary>
/// Notes that the store's build of the game is not the one the loadout was last applied to. A sync
/// rewrites <see cref="Loadout.LocatorIds"/> from a fresh locate before it applies, so a difference to a
/// fresh locate now means the game changed after the last apply. It stays quiet for a loadout that was
/// never applied, because nothing has been put in the game folder that could have gone stale. A suggestion
/// when the loadout holds no mods that hook the game's code, a warning naming them when it does.
/// </summary>
public class GameBuildChangedCheck : IGameUpdateCheck
{
    public IEnumerable<Diagnostic> Check(GameUpdateContext context)
    {
        var loadout = context.Loadout;
        if (loadout.LastAppliedDateTime == DateTimeOffset.MinValue) yield break;

        // Without ids on either side there is no build to compare, which is not the same as a changed build.
        var applied = loadout.LocatorIds.ToHashSet();
        var current = context.Fresh.LocatorIds;
        if (applied.Count == 0 || current.IsDefaultOrEmpty) yield break;
        if (applied.SetEquals(current)) yield break;

        var game = context.Installation.Game.DisplayName;
        var store = context.Fresh.Store.Value;
        var mods = UpdateNoteMods.Find(context);

        // Mods that load native code are the ones an update breaks outright, so with any installed the note is a warning.
        if (mods.Count == 0)
        {
            yield return Diagnostics.CreateGameBuildChanged(Game: game, Store: store, WhatBreaks: context.Sensitivity.WhatBreaks);
            yield break;
        }

        yield return Diagnostics.CreateGameBuildChangedWithMods(
            Game: game,
            Store: store,
            WhatBreaks: context.Sensitivity.WhatBreaks,
            ModCount: mods.Count,
            Mods: UpdateNoteMods.List(mods)
        );
    }
}

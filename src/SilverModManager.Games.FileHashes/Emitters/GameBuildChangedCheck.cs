using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Emitters;

/// <summary>
/// Notes that the store's build of the game is not the one the loadout was last applied to. A sync
/// rewrites <see cref="Loadout.LocatorIds"/> from a fresh locate before it applies, so a difference to a
/// fresh locate now means the game changed after the last apply. It stays quiet for a loadout that was
/// never applied, because nothing has been put in the game folder that could have gone stale.
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

        yield return Diagnostics.CreateGameBuildChanged(
            Game: context.Installation.Game.DisplayName,
            Store: context.Fresh.Store.Value,
            WhatBreaks: context.Sensitivity.WhatBreaks
        );
    }
}

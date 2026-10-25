using System.Globalization;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;

namespace SilverModManager.Games.FileHashes.Emitters;

/// <summary>
/// Notes that the store finished writing a game update within the window the game names. The date is when
/// this machine finished the write, not the publisher's, so a fresh install or a moved library counts too:
/// that is why it stays quiet for a loadout that was never applied, and for a loadout with no mod that hooks
/// the game's code, where an update has nothing to break.
/// </summary>
public class GameRecentlyUpdatedCheck(TimeProvider time) : IGameUpdateCheck
{
    private const string UpdateOnLaunchAdvice =
        "To take future updates on your own schedule, set the game's Steam update setting to \"Only update this game when I launch it\".";

    public IEnumerable<Diagnostic> Check(GameUpdateContext context)
    {
        if (context.Sensitivity.DateWindow is not { } window) yield break;
        if (context.Fresh.StoreLastWrite is not { } lastWrite) yield break;
        if (context.Loadout.LastAppliedDateTime == DateTimeOffset.MinValue) yield break;
        if (time.GetUtcNow() - lastWrite > window) yield break;

        var mods = UpdateNoteMods.Find(context);
        if (mods.Count == 0) yield break;

        yield return Diagnostics.CreateGameRecentlyUpdated(
            Game: context.Installation.Game.DisplayName,
            Store: context.Fresh.Store.Value,
            Date: lastWrite.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
            WhatBreaks: context.Sensitivity.WhatBreaks,
            Mods: UpdateNoteMods.List(mods),
            UpdateAdvice: context.Fresh.StoreUpdatesInBackground ? UpdateOnLaunchAdvice : ""
        );
    }
}

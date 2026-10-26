using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Controls.Spine;

/// <summary>
/// The name a loadout goes by where space is short (spine tooltips, the drop overlay). Most people have one loadout
/// per game and think of it as the game, so the loadout name only shows once there is a second one to tell apart.
/// </summary>
public static class LoadoutLabel
{
    /// <summary>The game's name, plus " - loadout name" when the same installation has other visible loadouts.</summary>
    public static string For(Loadout.ReadOnly loadout, IEnumerable<Loadout.ReadOnly> visibleLoadouts)
    {
        var game = loadout.InstallationInstance.Game.DisplayName;
        var installation = loadout.InstallationId;
        var siblings = visibleLoadouts.Count(other => other.InstallationId == installation);
        return siblings >= 2 ? $"{game} - {loadout.Name}" : game;
    }

    public static string For(Loadout.ReadOnly loadout, IDb db) => For(loadout, Loadout.All(db).Where(l => l.IsVisible()));
}

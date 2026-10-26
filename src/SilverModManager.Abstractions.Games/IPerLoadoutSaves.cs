using System.Text.RegularExpressions;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// Implemented by a game that can keep each loadout's saves in a slot of its own.
/// </summary>
/// <remarks>
/// A game that does not implement this has no per-loadout saves; callers read that as unsupported. Both methods
/// only plan: they read the loadout and return what would change, and the caller decides when to write.
/// </remarks>
public interface IPerLoadoutSaves
{
    /// <summary>
    /// How to point the game at the loadout's save slot, named by <see cref="LoadoutSaveSlot.IdOf"/>. Null when the
    /// game has nothing to redirect for this loadout.
    /// </summary>
    SaveRedirectPlan? PlanSaveRedirect(Loadout.ReadOnly loadout);

    /// <summary>
    /// The copies that would move the save the user plays today, found under <paramref name="saveRoot"/>, into the
    /// loadout's slot. Null when there is nothing to adopt. Run only after the user confirms.
    /// </summary>
    SaveAdoptionPlan? PlanAdoptCurrentSave(Loadout.ReadOnly loadout, AbsolutePath saveRoot);
}

/// <summary>
/// The name a loadout's saves go under.
/// </summary>
public static partial class LoadoutSaveSlot
{
    /// <summary>
    /// Matches exactly the ids <see cref="IdOf"/> returns: 14 lowercase hex digits. Scanners use it to tell the
    /// app's own <c>&lt;prefix&gt;_&lt;id&gt;</c> names from a save another tool wrote, which must be left alone.
    /// </summary>
    public const string IdPattern = @"^[0-9a-f]{14}\z";

    [GeneratedRegex(IdPattern)]
    private static partial Regex IdRegex();

    /// <summary>
    /// True when <paramref name="text"/> is a slot id this app would have generated.
    /// </summary>
    public static bool IsSlotId(string text) => IdRegex().IsMatch(text);

    /// <summary>
    /// The slot id for <paramref name="loadout"/>: its entity id without the partition byte, as fixed-width hex.
    /// </summary>
    /// <remarks>
    /// The entity id never changes, so a rename keeps the saves attached. The short name is not used because it is
    /// one or two letters, reused once a loadout is deleted, and not guaranteed unique.
    /// </remarks>
    public static string IdOf(Loadout.ReadOnly loadout)
    {
        return loadout.LoadoutId.Value.ValuePortion.ToString("x14", System.Globalization.CultureInfo.InvariantCulture);
    }
}

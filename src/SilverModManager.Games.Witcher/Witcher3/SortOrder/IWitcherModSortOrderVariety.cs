using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.SortOrder;

/// <summary>What the Witcher 3 Load Order offers beyond <see cref="ISortOrderVariety"/>.</summary>
public interface IWitcherModSortOrderVariety : ISortOrderVariety
{
    /// <summary>
    /// The folders, winner to loser, the locked merged folder first, each with its switch: the order mods.settings is
    /// rendered in. Taken from the loadout's own revision, so it is what the page shows for that revision.
    /// </summary>
    IReadOnlyList<(string Folder, bool IsEnabled)> StoredOrder(Loadout.ReadOnly loadout);

    /// <summary>
    /// The rank the merge engine uses for a folder: its groups' highest priority, or null for a folder the loadout
    /// does not supply. Reads model accessors only, so it is safe to call from inside a running query.
    /// </summary>
    ConflictPriority? RankOf(Loadout.ReadOnly loadout, string folder);
}

using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <param name="Group">The installed group the entry places.</param>
/// <param name="Enabled">Switch the group on (true) or off (false); null leaves it as it is.</param>
public sealed record ModOrderEntry(LoadoutItemGroupId Group, bool? Enabled);

/// <param name="Item">What the source named, as the user would recognise it (mod name, Vortex key, file name).</param>
/// <param name="Reason">Why it could not be placed, in words the user can act on.</param>
public sealed record ModOrderDrop(string Item, string Reason);

/// <summary>
/// An order of installed mod groups handed over by a collection or a Wabbajack list. It is a starting
/// order for the loadout, not a rule.
/// </summary>
/// <param name="WinnerToLoser">Highest priority first.</param>
/// <param name="Dropped">Entries the source named that could not be placed.</param>
public sealed record ModOrder(IReadOnlyList<ModOrderEntry> WinnerToLoser, IReadOnlyList<ModOrderDrop> Dropped)
{
    public static readonly ModOrder Empty = new([], []);

    /// <summary>Throws <see cref="ArgumentException"/> when a group appears twice.</summary>
    public static ModOrder Create(IEnumerable<ModOrderEntry> winnerToLoser, IEnumerable<ModOrderDrop> dropped)
    {
        var entries = winnerToLoser.ToArray();
        var seen = new HashSet<LoadoutItemGroupId>();
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Group))
                throw new ArgumentException($"Group {entry.Group} is listed twice in the mod order.", nameof(winnerToLoser));
        }
        return new ModOrder(entries, dropped.ToArray());
    }
}

/// <param name="Name">What to call the source in a card: "collection X", "Wabbajack list Y".</param>
/// <param name="Container">The collection or Wabbajack group the order was for.</param>
/// <param name="Manifest">The collection.json when the source is a collection; null for a Wabbajack list.</param>
/// <param name="SeedVarieties">Whether the sink may derive the game's sort orders from this mod order; false when the source carries its own (a Wabbajack profile).</param>
public sealed record ModOrderSource(string Name, LoadoutItemGroupId Container, CollectionRoot? Manifest, bool SeedVarieties = true);

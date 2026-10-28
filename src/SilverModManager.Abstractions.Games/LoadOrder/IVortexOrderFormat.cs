using System.Text.Json;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>A game's Vortex per-game order key, read and written by one class so the shape is described once.</summary>
public interface IVortexOrderFormat
{
    /// <summary>The manifest key the order lives under, as <see cref="VortexOrderKeys"/> knows it.</summary>
    string Key { get; }

    /// <summary>Entries winner first, plus the keys that could not be mapped to a mods[] entry (each becomes a drop).</summary>
    (IReadOnlyList<ModOrderSourceEntry> Entries, IReadOnlyList<ModOrderDrop> Unmapped) Read(JsonElement value, CollectionRoot root);

    /// <summary>
    /// A <c>mergedData</c> value to write beside the key, for a game whose Vortex importer fails without one; null for none.
    /// </summary>
    JsonElement? MergedData => null;

    /// <summary>The key's value for a loadout's order (winner first), or null when nothing in it is representable.</summary>
    JsonElement? Write(Loadout.ReadOnly loadout, IReadOnlyList<LoadoutItemGroup.ReadOnly> winnerToLoser);
}

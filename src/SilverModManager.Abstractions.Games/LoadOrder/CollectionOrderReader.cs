using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>Reads the order a collection manifest carries and resolves it against the groups installed in the collection.</summary>
public static class CollectionOrderReader
{
    /// <summary>
    /// Our key when present; else the game's Vortex key through its <see cref="IVortexOrderFormat"/> when the game has one and
    /// the manifest carries that key; else <see cref="ModOrder.Empty"/>. The Vortex key is ignored (no drops) when ours is present.
    /// </summary>
    public static ModOrder Read(IDb db, IGame game, CollectionRoot root, LoadoutItemGroupId container)
    {
        if (root.SilverLoadOrder is { } ours)
        {
            var entries = ours.Select(entry => new ModOrderSourceEntry(
                new ModOrderReference(entry.Md5, entry.ModId, entry.FileId, string.IsNullOrWhiteSpace(entry.Name) ? null : entry.Name),
                entry.Enabled)).ToArray();
            return ModOrderMatcher.Match(db, container, entries);
        }

        if (game is IModOrderGame { VortexOrderFormat: { } format } && VortexOrderKeys.Get(root, format.Key) is { } value)
        {
            var (entries, unmapped) = format.Read(value, root);
            var matched = ModOrderMatcher.Match(db, container, entries.ToArray());
            return ModOrder.Create(matched.WinnerToLoser, unmapped.Concat(matched.Dropped));
        }

        return ModOrder.Empty;
    }
}

using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games;

/// <summary>An enabled mod of a loadout and the files of it that hook the game's code.</summary>
public sealed record NativeCodeMod(string Name, IReadOnlyList<GamePath> Files);

/// <summary>
/// Finds the mods of a loadout that hook the game's code, for the notes that tell the user a game update may have
/// broken them. It reads the loadout, not the game folder, so files placed there by hand are not found.
/// </summary>
public static class NativeCodeMods
{
    /// <summary>
    /// The enabled mods of <paramref name="loadout"/> with at least one file <paramref name="game"/> says hooks its
    /// code, by name. Empty when the loadout's game does not implement <see cref="IGameNativeCode"/>.
    /// </summary>
    public static IReadOnlyList<NativeCodeMod> Find(Loadout.ReadOnly loadout)
        => loadout.InstallationInstance.Game is IGameNativeCode game ? Find(loadout, game) : [];

    /// <inheritdoc cref="Find(Loadout.ReadOnly)"/>
    public static IReadOnlyList<NativeCodeMod> Find(Loadout.ReadOnly loadout, IGameNativeCode game)
    {
        var found = new List<NativeCodeMod>();
        foreach (var group in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemGroup())
        {
            if (!group.AsLoadoutItem().IsEnabled()) continue;

            // Files, not subgroups: a collection group holds mod groups, and each of those is named on its own.
            var files = group.Children
                .OfTypeLoadoutItemWithTargetPath()
                .OfTypeLoadoutFile()
                .Select(file => (GamePath)file.AsLoadoutItemWithTargetPath().TargetPath)
                .Where(target => game.HooksGameCode(target))
                .Order()
                .ToArray();
            if (files.Length == 0) continue;

            found.Add(new NativeCodeMod(group.AsLoadoutItem().Name, files));
        }

        return found.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

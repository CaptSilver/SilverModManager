using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.Diagnostics;

/// <summary>
/// A mod's <c>.part.txt</c> names the settings file its keys belong in. The game reads three of them, and only those
/// are baked, so a fragment for any other name (a typo, a file a patch added, <c>input.xml.part.txt</c>) would
/// otherwise leave its menu unmodded without a word. A fragment for one of the three that is not on disk yet has no
/// player copy to build on, so it waits for the game to create the file. When the loadout does not own the game's
/// INIs none of the fragments is merged, and each mod that has any gets a card saying so.
/// </summary>
public sealed class DroppedSettingsPartEmitter : ADroppedItemEmitter
{
    private const string UnreadReason = "The Witcher 3 reads only input.settings, user.settings and dx12user.settings";

    protected override async IAsyncEnumerable<DroppedItem> Dropped(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var (target, fragment) in IniTweakLayers.EnabledForOtherTargets(loadout, WitcherConstants.W3SettingsFiles))
        {
            var reason = target.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                ? $"it is an XML fragment for the menu's {target}, and the app only merges .settings files"
                : UnreadReason;
            yield return new DroppedItem($"the {target} fragment", fragment.SourceName, reason, "Library");
        }

        if (!loadout.IsValid()) yield break;

        // The fragments are merged into the settings files the loadout writes. Without that there is nothing to merge
        // them into, so each mod's say so rather than going unused without a word.
        if (!Loadout.OwnsGameInis.Get(loadout))
        {
            foreach (var card in NotOwned(loadout)) yield return card;
            yield break;
        }

        // user.settings belongs to DX11 and dx12user.settings to DX12, so a player has one of them. The other renderer's
        // fragments are not waiting for anything; they only wait when the game was never started.
        foreach (var files in new[] { new[] { "input.settings" }, new[] { "user.settings", "dx12user.settings" } })
        {
            if (files.Any(file => Exists(loadout, file))) continue;

            var mods = files
                .SelectMany(file => IniTweakLayers.All(loadout, new GamePath(WitcherConstants.WitcherDocuments, file)))
                .Where(t => t.IsEnabled)
                .Select(t => t.SourceName).Distinct().Order(StringComparer.Ordinal).ToList();
            if (mods.Count == 0) continue;

            var what = files.Length == 1 ? $"{files[0]} doesn't exist yet" : $"neither {files[0]} nor {files[1]} exists yet";
            yield return new DroppedItem($"the settings for {string.Join(" and ", files)}", string.Join(", ", mods),
                $"{what}. Start the game once, then Apply again", null);
        }
    }

    private static IEnumerable<DroppedItem> NotOwned(Loadout.ReadOnly loadout)
    {
        var filesByMod = WitcherConstants.W3SettingsFiles
            .SelectMany(file => IniTweakLayers.All(loadout, new GamePath(WitcherConstants.WitcherDocuments, file)).Where(t => t.IsEnabled).Select(t => (Mod: t.SourceName, File: file)))
            .GroupBy(t => t.Mod, t => t.File, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var mod in filesByMod)
        {
            var files = mod.Distinct().Order(StringComparer.Ordinal).ToArray();
            yield return new DroppedItem($"the {string.Join(" and ", files)} fragment", mod.Key,
                "this loadout does not own the game's settings files, so nothing is merged into them. Turn on \"Own the game INIs\" in the loadout's settings", null);
        }
    }

    private static bool Exists(Loadout.ReadOnly loadout, string file)
    {
        var path = new GamePath(WitcherConstants.WitcherDocuments, file);
        var locations = loadout.InstallationInstance.Locations;
        return GameInis.StateOf(loadout, path) is not null
               || (locations.ContainsKey(path.LocationId) && locations.ToAbsolutePath(path).FileExists);
    }
}

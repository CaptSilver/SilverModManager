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
/// player copy to build on, so it waits for the game to create the file.
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

        if (!Loadout.OwnsGameInis.Get(loadout) || !loadout.IsValid()) yield break;

        var locations = loadout.InstallationInstance.Locations;
        foreach (var file in WitcherConstants.W3SettingsFiles)
        {
            var path = new GamePath(WitcherConstants.WitcherDocuments, file);
            if (GameInis.StateOf(loadout, path) is not null) continue;
            if (locations.ContainsKey(path.LocationId) && locations.ToAbsolutePath(path).FileExists) continue;

            var mods = IniTweakLayers.All(loadout, path).Where(t => t.IsEnabled).Select(t => t.SourceName).Distinct().Order(StringComparer.Ordinal).ToList();
            if (mods.Count == 0) continue;

            yield return new DroppedItem($"the settings for {file}", string.Join(", ", mods),
                $"{file} doesn't exist yet. Start the game once, then Apply again", "Library");
        }
    }
}

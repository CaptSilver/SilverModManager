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
/// otherwise leave its menu unmodded without a word.
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
                ? $"an XML fragment for the menu's {target}; the app does not merge menu XML, and Vortex drops these too (menumod.ts:104-105 excludes them from its settings merge; its XML merger takes only .xml files)"
                : UnreadReason;
            yield return new DroppedItem($"{fragment.Name}'s {target} fragment", fragment.SourceName, reason, "Library");
        }
    }
}

using System.Collections.Frozen;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.Diagnostics;

/// <summary>
/// Says that the mods.settings on disk was not the one the app wrote and has been replaced by the Load Order's. The
/// notice that backs the card is retracted when the user next drags a folder or flips a switch.
/// </summary>
public sealed class ModsSettingsReplacedEmitter : ADroppedItemEmitter
{
    protected override IAsyncEnumerable<DroppedItem> Dropped(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, CancellationToken cancellationToken) =>
        ModsSettingsNotices.For(loadout.Db, loadout.LoadoutId)
            .Select(notice => new DroppedItem("mods.settings", "the copy in Documents/The Witcher 3", notice.Summary, "Load Order"))
            .ToAsyncEnumerable();
}

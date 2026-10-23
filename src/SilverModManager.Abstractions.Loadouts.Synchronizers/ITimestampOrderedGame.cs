using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// A game that reads its load order from the modification times of its plugin files. The synchronizer sets the
/// times it is given while deploying and records them in the disk state, so the next index does not see the
/// files as edited.
/// </summary>
public interface ITimestampOrderedGame
{
    /// <summary>
    /// The time each plugin file should carry once the apply is done. The plugins the loadout will have on disk
    /// go in the list, one time each; the sync tree is the state before the apply.
    /// </summary>
    Task<IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)>> PluginTimestamps(Loadout.ReadOnly loadout, IReadOnlyDictionary<GamePath, SyncNode> syncTree);
}

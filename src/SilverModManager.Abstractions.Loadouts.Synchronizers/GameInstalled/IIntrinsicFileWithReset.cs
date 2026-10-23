using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// An intrinsic file that can say what it should contain when the game is reset to its original state.
/// </summary>
public interface IIntrinsicFileWithReset : IIntrinsicFile
{
    /// <summary>
    /// The bytes the file should have after a reset, or null to leave it alone. Takes <see cref="SyncNode"/>s
    /// rather than node parts because the content depends on who owns each path and what the sync will do to it.
    /// </summary>
    Task<byte[]?> RenderForReset(Loadout.ReadOnly lastSyncedLoadout, IReadOnlyDictionary<GamePath, SyncNode> desiredState);
}

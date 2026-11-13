using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Takes files out of a game folder in a way the file store can undo: the bytes are stored first, then the file
/// leaves the disk. Delete and Set aside both go through it, and both must apply the loadout afterwards.
/// </summary>
[PublicAPI]
public interface IGameFolderFileRemoval
{
    /// <summary>
    /// Makes sure the store holds the bytes of every file. Refuses before writing anything when a file on disk differs
    /// from its identity, or is gone while the store lacks its bytes; backs up the ones the store lacks.
    /// </summary>
    /// <exception cref="GameFolderFileRemovalRefusedException">A file is not what its row says it is.</exception>
    Task EnsureStored(Loadout.ReadOnly loadout, IReadOnlyList<(GamePath Path, FileIdentity Identity)> files, CancellationToken token);

    /// <summary>
    /// Moves each file to the trash, or deletes it when the trash is on another file system and refuses the move.
    /// Call after <see cref="EnsureStored"/>: a deleted file can then be restored from the store. A path with no file
    /// on disk is skipped.
    /// </summary>
    Task<RemovedFromDisk> TakeOffDisk(Loadout.ReadOnly loadout, IReadOnlyList<GamePath> paths, CancellationToken token);
}

/// <summary>What <see cref="IGameFolderFileRemoval.TakeOffDisk"/> did with each path.</summary>
/// <param name="Trashed">Paths that went to the trash.</param>
/// <param name="Deleted">Paths that were deleted because the trash refused them.</param>
public sealed record RemovedFromDisk(IReadOnlyList<GamePath> Trashed, IReadOnlyList<GamePath> Deleted);

/// <summary>A removal from the game folder that was refused before any file was touched.</summary>
[PublicAPI]
public sealed class GameFolderFileRemovalRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "Nothing was removed";
}

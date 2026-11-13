using JetBrains.Annotations;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Puts files the player left in the game folder out of the game's reach without losing them: the Overrides rows move
/// into a disabled group, so enabling the group puts the files back.
/// </summary>
[PublicAPI]
public interface ISetAsideFiles
{
    /// <summary>The name the set-aside group gets when it is created. Later lookups go by its marker, not this name.</summary>
    public const string GroupName = "Set aside by Health Check";

    /// <summary>
    /// Moves the Overrides rows at <paramref name="paths"/> into the loadout's set-aside group, creating it disabled when
    /// absent, takes the files off disk and applies the loadout. The apply is what makes a later enable or revert bring
    /// the files back instead of recording them as deleted. Refuses before writing when a path is not an Overrides file
    /// row, or when its bytes on disk changed since the last sync.
    /// </summary>
    /// <exception cref="OverridesMoveRefusedException">A path has no file row in the Overrides group.</exception>
    /// <exception cref="GameFolderFileRemovalRefusedException">A file on disk is not what its row says.</exception>
    Task<SetAsideResult> SetAside(Loadout.ReadOnly loadout, IReadOnlyList<GamePath> paths, CancellationToken token);
}

/// <summary>What <see cref="ISetAsideFiles.SetAside"/> did.</summary>
/// <param name="Moved">Paths whose rows now sit in the set-aside group.</param>
/// <param name="Group">The set-aside group.</param>
/// <param name="RestorePoint">The transaction written before anything changed; reverting to it undoes the move.</param>
/// <param name="Applied">
/// False when the apply that follows the move was refused (the game is running, a prompt was declined). The files are
/// then already moved and off disk, and the next apply finishes the job.
/// </param>
public sealed record SetAsideResult(IReadOnlyList<GamePath> Moved, LoadoutItemGroupId Group, TxId RestorePoint, bool Applied);

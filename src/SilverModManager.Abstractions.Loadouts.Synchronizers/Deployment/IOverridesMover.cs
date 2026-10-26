using JetBrains.Annotations;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Moves the files in a loadout's Overrides group (External Changes) into a mod, so they stop being loose
/// edits and take part in the loadout's conflict order like any other mod file.
/// </summary>
[PublicAPI]
public interface IOverridesMover
{
    /// <summary>
    /// Adds an empty top-level group, with the priority row that lets it take part in conflicts, in its own
    /// transaction. Meant as the destination for <see cref="Move"/> when the files do not belong to an existing mod.
    /// </summary>
    Task<LoadoutItemGroupId> CreateEmptyGroup(LoadoutId loadoutId, string name);

    /// <summary>
    /// For each path, what moving the Overrides file into <paramref name="target"/> would do. Reads only.
    /// </summary>
    OverridesMovePreview[] Preview(LoadoutId loadoutId, GamePath[] paths, LoadoutItemGroupId target);

    /// <summary>
    /// Moves the Overrides rows at <paramref name="paths"/> into <paramref name="target"/> in one transaction.
    /// Throws <see cref="OverridesMoveRefusedException"/>, before writing anything, if the target is not a valid
    /// destination, a path has no row in the Overrides group, or the game refuses one of the paths.
    /// </summary>
    Task<OverridesMoveResult> Move(LoadoutId loadoutId, GamePath[] paths, LoadoutItemGroupId target, MoveOptions options);
}

/// <summary>How <see cref="IOverridesMover.Move"/> treats paths the target already has and what it records.</summary>
public sealed record MoveOptions
{
    /// <summary>
    /// When the target already has a file at a moved path, replace it with the moved one. When false that path is
    /// left in the Overrides group and reported as skipped.
    /// </summary>
    public bool ReplaceExisting { get; init; } = true;

    /// <summary>
    /// Also pins each moved path to the target, so the moved file wins its path whatever the group order says.
    /// </summary>
    public bool PinMovedPaths { get; init; }
}

/// <summary>What <see cref="IOverridesMover.Move"/> did.</summary>
/// <param name="Moved">Paths whose rows now sit in the target.</param>
/// <param name="Skipped">Paths left in the Overrides group because the target already had them and replacing was off.</param>
public sealed record OverridesMoveResult(GamePath[] Moved, GamePath[] Skipped);

/// <summary>
/// What a move of one Overrides file into a target group would do.
/// </summary>
/// <param name="Path">The path asked about.</param>
/// <param name="InOverrides">Whether the Overrides group has a row at the path. When false the rest is empty.</param>
/// <param name="IsDelete">Whether that row records a deleted file rather than content.</param>
/// <param name="Hash">The content hash of the Overrides file, absent for a delete.</param>
/// <param name="TargetHasPath">Whether the target group already has a row at the path.</param>
/// <param name="TargetIsDelete">Whether the target's row at the path is a delete marker.</param>
/// <param name="TargetHash">The content hash of the target's file at the path, absent when it has none or is a delete.</param>
/// <param name="WinnerAfterMove">
/// The group whose row would provide the path once the move is done: the target when it outranks every other
/// enabled group with the path, otherwise the group it would lose to. Absent when the target is disabled and
/// nothing else has the path.
/// </param>
/// <param name="WinsAfterMove">Whether <see cref="WinnerAfterMove"/> is the target.</param>
public sealed record OverridesMovePreview(
    GamePath Path,
    bool InOverrides,
    bool IsDelete,
    Hash? Hash,
    bool TargetHasPath,
    bool TargetIsDelete,
    Hash? TargetHash,
    LoadoutItemGroupId? WinnerAfterMove,
    bool WinsAfterMove);

/// <summary>
/// A move out of the Overrides group that was refused: the destination is not allowed, a path is no longer in the
/// group, or the game does not allow the file to be moved.
/// </summary>
[PublicAPI]
public class OverridesMoveRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The files were not moved";
}

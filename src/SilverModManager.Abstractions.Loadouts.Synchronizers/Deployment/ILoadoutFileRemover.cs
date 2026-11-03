using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Takes file rows out of a loadout. The Apply preview, a mod's files page and the command line all delete through
/// this, so they agree on which rows count, what cannot go, and how the removal can be undone.
/// </summary>
[PublicAPI]
public interface ILoadoutFileRemover
{
    /// <summary>
    /// Every row the loadout has at or under the request's paths, with who provides it and why it cannot go. Reads only.
    /// </summary>
    FileRemovalPlan Plan(FileRemovalRequest request);

    /// <summary>
    /// Plans the request again, writes a restore point, then deletes the removable rows (and the locked ones when
    /// asked) in one transaction. Throws <see cref="FileRemovalRefusedException"/> before writing when nothing would
    /// be removed.
    /// </summary>
    Task<FileRemovalResult> Remove(FileRemovalRequest request, bool includeLocked);
}

/// <summary>
/// Paths are exact files or folders. <paramref name="OnlyInGroups"/> scopes the search to those groups (the files
/// page, the CLI group verb); null is the whole loadout (the preview).
/// </summary>
public sealed record FileRemovalRequest(LoadoutId LoadoutId, GamePath[] Paths, LoadoutItemGroupId[]? OnlyInGroups = null);

/// <summary>What a <see cref="FileRemovalRequest"/> finds, split by whether the rows may go.</summary>
public sealed record FileRemovalPlan(FileRemovalRequest Request, IReadOnlyList<FileRemovalRow> Rows)
{
    /// <summary>Rows that go whatever the caller asks.</summary>
    public IEnumerable<FileRemovalRow> Removable => Rows.Where(r => r.Blocker is null && r.LockReason is null);

    /// <summary>Rows inside a collection that decides what it contains; they go only when the caller says so.</summary>
    public IEnumerable<FileRemovalRow> Locked => Rows.Where(r => r.Blocker is null && r.LockReason is not null);

    /// <summary>Rows that never go through this service.</summary>
    public IEnumerable<FileRemovalRow> Blocked => Rows.Where(r => r.Blocker is not null);

    /// <summary>Whether <see cref="ILoadoutFileRemover.Remove"/> would delete anything for this plan.</summary>
    public bool HasAnythingToRemove(bool includeLocked) => Removable.Any() || (includeLocked && Locked.Any());

    /// <summary>
    /// What <see cref="ILoadoutFileRemover.Remove"/> says when <see cref="HasAnythingToRemove"/> is false: one
    /// sentence per distinct blocker, per locked collection, or, when no row was found, per requested path.
    /// </summary>
    public string RefusalMessage()
    {
        var lines = new List<string>();
        lines.AddRange(Blocked.Select(r => r.Blocker).OfType<string>().Distinct());
        lines.AddRange(Locked.Select(r => $"`{r.Path.Path}` belongs to {r.CollectionName}, which stays as it is unless locked files are included.").Distinct());
        if (lines.Count == 0)
            lines.AddRange(Request.Paths.Distinct().Select(p => $"Nothing in the loadout provides `{p.Path}`."));
        return string.Join('\n', lines);
    }
}

/// <summary>
/// One loadout row at a path.
/// </summary>
/// <param name="RowId">The row's entity id.</param>
/// <param name="Path">Where the row puts its file.</param>
/// <param name="GroupName">The name of the group that holds the row.</param>
/// <param name="GroupId">The group that holds the row.</param>
/// <param name="CollectionName">The first collection on the row's walk up the parents, null when there is none.</param>
/// <param name="LockReason"><see cref="ICollectionOwnership.WhyLocked"/> for that collection, null when the user owns it.</param>
/// <param name="Blocker">A sentence when the row is never removable here, null otherwise.</param>
/// <param name="IsDisabled">The row or one of its parents is switched off.</param>
/// <param name="IsExternalChange">The row sits in the Overrides group.</param>
/// <param name="IsWinner">Whether this row is what the preview shows for the path.</param>
public sealed record FileRemovalRow(
    EntityId RowId,
    GamePath Path,
    string GroupName,
    LoadoutItemGroupId GroupId,
    string? CollectionName,
    string? LockReason,
    string? Blocker,
    bool IsDisabled,
    bool IsExternalChange,
    bool IsWinner);

/// <summary>What <see cref="ILoadoutFileRemover.Remove"/> did.</summary>
/// <param name="Removed">Paths whose rows were deleted.</param>
/// <param name="RestorePoint">
/// The revision written before the removal; <c>loadout revert -r</c> with it brings the rows back.
/// </param>
public sealed record FileRemovalResult(IReadOnlyList<GamePath> Removed, TxId RestorePoint);

/// <summary>A removal that was refused before anything was written.</summary>
[PublicAPI]
public sealed class FileRemovalRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "Nothing was removed";
}

using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// An Apply deploys each folder of a game that runs under Wine in one spelling. Paths compare without regard to case in
/// C# and in the database's path attribute, but not in the SQL that picks the winning file, and not on a Linux disk, so a
/// list authored on Windows that spells a folder two ways leaves two folders on disk and two winners for one file.
/// </summary>
/// <remarks>
/// Why twins appear. <see cref="BuildSyncTree{T}"/> adds the loadout's rows first and the disk after, and a node is
/// keyed, and so written and deleted, in the spelling of whichever added it first: the row's. A row spelled differently
/// from the disk therefore writes a second folder on a case-sensitive disk. The nodes of the game layer use the hash
/// store's spelling, intrinsic files the spelling in the game's code, and a reset the spelling of the initial state; none
/// of those are loadout rows. Respelling the rows keeps the recorded state in agreement with the disk, and
/// <see cref="RespellTree"/> puts the writes that do not come from rows in the same spelling. Both are needed.
/// </remarks>
public partial class ALoadoutSynchronizer
{
    /// <summary>
    /// Top folders of the game folder that always have the spelling listed here, whatever the loadout or disk holds, on
    /// every platform. A game that has such folders lists them (Witcher 3: Mods, dlc, bin).
    /// </summary>
    protected virtual IReadOnlyList<string> FixedFolderSpellings => [];

    /// <inheritdoc />
    public bool MergesCaseVariantFoldersOnApply(GameInstallation installation)
        => CaseVariantFolders.RunsUnderWine(installation) || FixedFolderSpellings.Count > 0;

    /// <summary>The spellings of the apply in progress; the writes that do not come from rows are mapped through them.</summary>
    private FolderSpellings? _applySpellings;

    private FolderSpellings BuildSpellings(GameInstallation installation, EntityId? loadout, GameInstallMetadata.ReadOnly metadata, bool afterMerge = false)
    {
        var underWine = CaseVariantFolders.RunsUnderWine(installation);
        var filter = GamePathFilter;
        var attested = underWine ? _fileHashService.AttestedFiles(installation) : null;

        IEnumerable<GamePath> folders = [];
        IEnumerable<(GamePath, bool)> files = [];
        folders = DiskFolders(installation, underWine);
        if (underWine)
        {
            files = GetDiskStateForGame(metadata).Select(pair =>
            {
                var vanilla = attested is not null && attested.TryGetValue(pair.Path, out var a)
                    && a.Size == pair.Part.Size && a.ModifiedUtc.UtcTicks == pair.Part.LastModifiedTicks;
                return (pair.Path, vanilla);
            }).Where(f => !filter.ShouldFilter(f.Item1)).ToList();
        }

        IEnumerable<GamePath> targets = underWine && loadout is { } id
            ? GamePathRows.TargetPaths(Connection.Db, id).Select(r => r.GamePath).Where(p => !filter.ShouldFilter(p)).ToList()
            : [];
        return FolderSpellings.Build(new FolderSpellingSources(FixedFolderSpellings, underWine, folders, files, targets, KeepSplitFolders: afterMerge));
    }

    /// <summary>The directories on disk, not following symlinks, with filtered paths left out.</summary>
    private IEnumerable<GamePath> DiskFolders(GameInstallation installation, bool recursive)
    {
        var result = new List<GamePath>();
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = recursive, IgnoreInaccessible = true };
        foreach (var (location, root) in installation.Locations.GetTopLevelLocations())
        {
            if (!root.DirectoryExists()) continue;
            if (!recursive && location != LocationId.Game) continue;

            var rootPath = root.GetFullPath();
            foreach (var directory in Directory.EnumerateDirectories(rootPath, "*", options))
            {
                var path = new GamePath(location, RelativePath.FromUnsanitizedInput(Path.GetRelativePath(rootPath, directory)));
                if (!GamePathFilter.ShouldFilter(path)) result.Add(path);
            }
        }
        return result;
    }

    /// <summary>
    /// The copy to keep when two files differ only in the case of their path: the one in the chosen spelling. Built the
    /// first time the indexer meets a twin, from the disk alone, so a reindex from a diagnostic and one from an apply
    /// agree on which copy owns the row.
    /// </summary>
    private Func<GamePath, bool>? PreferredCopy(GameInstallation installation, GameInstallMetadata.ReadOnly metadata)
    {
        if (!MergesCaseVariantFoldersOnApply(installation)) return null;

        FolderSpellings? spellings = null;
        return path => (spellings ??= BuildSpellings(installation, null, metadata)).IsCanonical(path);
    }

    /// <summary>
    /// Everything an apply does about case before it reads the disk state: finish a rewrite a crash left half done, merge
    /// the twin folders on disk, reindex, rewrite the recorded paths to the disk's spelling. Holds the lock throughout:
    /// a reindex from diagnostics in the middle would record half-moved files. Returns the metadata after the reindex.
    /// </summary>
    private async Task<GameInstallMetadata.ReadOnly> FoldAndReindex(GameInstallation installation, EntityId loadoutId, SynchronizeLoadoutJob? job, bool alwaysReindex, CancellationToken cancellationToken)
    {
        using var _ = await _lock.LockAsync();
        _applySpellings = null;
        if (!MergesCaseVariantFoldersOnApply(installation))
        {
            return alwaysReindex ? await ReindexStateCore(installation, job, cancellationToken) : _gameRegistry.ForceGetMetadata(installation);
        }

        var metadata = _gameRegistry.ForceGetMetadata(installation);
        var loadouts = Loadout.All(Connection.Db).Where(l => l.InstallationId.Value == metadata.Id).Select(l => l.LoadoutId).ToArray();

        // Rows a crash left marked are finished first: a reindex reads a marked disk row as a removed file.
        await GamePathRespeller.FinishPending(Connection, metadata.Id, loadouts, Logger);
        metadata = _gameRegistry.ForceGetMetadata(installation);

        var spellings = BuildSpellings(installation, loadoutId, metadata);
        var reindex = alwaysReindex;
        if (spellings.NeedsDiskMerge)
        {
            var merge = await new CaseTwinMerger(_fullHasher, _fileStore, Logger, GamePathFilter)
                .MergeTwins(installation, spellings, ManagedPaths(installation, loadoutId, metadata), cancellationToken);

            // A row whose file the merge replaced is dropped BEFORE the reindex, which then records the canonical file
            // afresh. Dropped after, the last applied state would still hold the row while the path has none on disk, and
            // "no disk, previous present, previous equals loadout" is a user deleting the file.
            await DropRows(metadata, merge.NeedsFreshRead);
            reindex |= merge.Moved.Count > 0 || merge.Deduplicated.Count > 0 || merge.NeedsFreshRead.Count > 0;
        }

        if (reindex) metadata = await ReindexStateCore(installation, job, cancellationToken);

        // Rebuilt from the disk as the merge left it, and a folder it left split stays as spelled: rows must agree with the
        // disk, so they are not moved to a spelling the disk does not have.
        if (reindex || spellings.NeedsDiskMerge) spellings = BuildSpellings(installation, loadoutId, metadata, afterMerge: true);

        var respelled = await GamePathRespeller.Respell(Connection, LoadoutId.From(loadoutId), metadata.Id, spellings, GamePathFilter);
        if (respelled > 0) Logger.LogInformation("Rewrote {Count} recorded paths to the spelling of the folders on disk", respelled);
        _applySpellings = spellings;
        return GameInstallMetadata.Load(Connection.Db, metadata.Id);
    }

    /// <summary>
    /// What the loadout deploys or has deployed, with the bytes wanted at each path where known: every target path, what
    /// the last apply put on disk, and the winning files.
    /// </summary>
    private Dictionary<GamePath, FileIdentity> ManagedPaths(GameInstallation installation, EntityId loadoutId, GameInstallMetadata.ReadOnly metadata)
    {
        var managed = new Dictionary<GamePath, FileIdentity>();
        foreach (var row in GamePathRows.TargetPaths(Connection.Db, loadoutId)) managed[row.GamePath] = FileIdentity.None;
        foreach (var pair in ((ILoadoutSynchronizer)this).GetPreviouslyAppliedDiskState(metadata)) managed[pair.Path] = pair.Part.Identity;
        foreach (var tuple in WinningFilesQuery(Connection.Db, LoadoutId.From(loadoutId)))
        {
            var identity = ParseIdentity(tuple.Identity);
            if (!identity.IsNone) managed[new GamePath(tuple.Location, tuple.Path)] = identity;
        }
        return managed;
    }

    private async ValueTask DropRows(GameInstallMetadata.ReadOnly metadata, IReadOnlyList<GamePath> paths)
    {
        if (paths.Count == 0) return;

        var wanted = paths.ToHashSet();
        using var tx = Connection.BeginTransaction();
        foreach (var row in GamePathRows.DiskStatePaths(Connection.Db, metadata.Id))
        {
            if (wanted.Contains(row.GamePath)) tx.Delete(row.Row, recursive: false);
        }
        await tx.Commit();
    }

    /// <summary>
    /// Writes each node of the tree in the spelling the apply chose. Rows already match, so for them this is the identity;
    /// it catches the nodes that do not come from rows (the game layer, intrinsic files, a reset's initial state), which
    /// would recreate a twin folder. A node can be re-keyed because paths compare without regard to case: the entry keeps
    /// its place and only the spelling it is written under changes.
    /// </summary>
    internal static void RespellTree(Dictionary<GamePath, SyncNode> tree, FolderSpellings? spellings)
    {
        if (spellings is null) return;

        List<GamePath>? moved = null;
        foreach (var path in tree.Keys)
        {
            if (!spellings.IsCanonical(path)) (moved ??= []).Add(path);
        }

        if (moved is null) return;
        foreach (var path in moved)
        {
            tree.Remove(path, out var node);
            tree[spellings.Canonical(path)] = node;
        }
    }
}

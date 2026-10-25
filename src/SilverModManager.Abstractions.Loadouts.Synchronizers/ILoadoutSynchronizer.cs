using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using OneOf;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// A Loadout Synchronizer is responsible for synchronizing loadouts between to and from the game folder.
/// </summary>
public interface ILoadoutSynchronizer
{
    /// <summary>
    /// Paths the synchronizer ignores completely: they are not indexed, backed up, deployed or deleted,
    /// and loadout items that target them are left out of the sync tree.
    /// </summary>
    IGamePathFilter GamePathFilter { get; }

    /// <summary>
    /// Whether the sync tree leaves out the loadout item's file at <paramref name="path"/>: the path is
    /// protected and neither the item nor a group it sits in is flagged to deploy to protected paths.
    /// </summary>
    bool IsSkippedAsProtected(LoadoutItem.ReadOnly item, GamePath path);

    /// <summary>
    /// Throws <see cref="SynchronizerPreflightException"/> if the game cannot be written to at all, without
    /// reading or writing anything. Every operation that changes the game folder or the loadout state calls
    /// it first; callers that are about to record something about the game (adding it) call it before they do.
    /// </summary>
    Task Preflight(GameInstallation installation);

    /// <summary>
    /// Creates a new sync tree from the current state of the game folder, the loadout and the previous state. This
    /// sync tree contains a matching of all the files in all 3 sources based on their path.
    /// </summary>
    void MergeStates(IEnumerable<PathPartPair> currentState, IEnumerable<PathPartPair> previousTree, Dictionary<GamePath, SyncNode> loadoutItems);
    
    /// <summary>
    /// Builds a sync tree from a loadout and the current state of the game folder.
    /// </summary>
    Task<Dictionary<GamePath, SyncNode>> BuildSyncTree(Loadout.ReadOnly loadoutTree, SynchronizeLoadoutJob? job = null);

    /// <summary>
    /// Builds a sync tree from the latest stored disk state and the previous disk state.
    /// </summary>
    Dictionary<GamePath, SyncNode> BuildSyncTree<T>(T latestDiskState, T previousDiskState, Loadout.ReadOnly loadout) where T : IEnumerable<PathPartPair>;
    
    /// <summary>
    /// Processes the sync tree to create the signature and actions for each file, changes are made in-place on the tree.
    /// </summary>
    void ProcessSyncTree(Dictionary<GamePath, SyncNode> syncTree);
    
    /// <summary>
    /// Run the groupings on the game folder and return a new loadout with the changes applied.
    /// </summary>
    Task<Loadout.ReadOnly> RunActions(Dictionary<GamePath, SyncNode> syncTree, Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job = null);
    
    /// <summary>
    /// Synchronizes the loadout with the game folder, any changes in the game folder will be added to the loadout, and any
    /// new changes in the loadout will be applied to the game folder.
    /// </summary>
    Task<Loadout.ReadOnly> Synchronize(Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job = null);

    /// <summary>
    /// Get the disk state for a game as of a specific transaction.
    /// </summary>
    /// <param name="metadata"></param>
    /// <param name="asOfTxId"></param>
    /// <returns></returns>
    public List<PathPartPair> GetDiskStateForGameAsOf(Sdk.Games.GameInstallMetadata.ReadOnly metadata, TxId asOfTxId)
    {
        var db = metadata.Db.Connection.AsOf(asOfTxId);
        var oldMetadata = Sdk.Games.GameInstallMetadata.Load(db, metadata.Id);
        return GetDiskStateForGame(oldMetadata);
    }

    /// <summary>
    /// Gets the previously applied disk state for a game.
    /// </summary>
    public List<PathPartPair> GetPreviouslyAppliedDiskState(Sdk.Games.GameInstallMetadata.ReadOnly metadata)
    {
        List<PathPartPair> prevItems;
        if (!metadata.Contains(Sdk.Games.GameInstallMetadata.LastSyncedLoadout))
        {
            prevItems = [];
        }
        else
        {
            var txId = Sdk.Games.GameInstallMetadata.LastSyncedLoadoutTransactionId.Get(metadata);
            var asOfDb = metadata.Db.Connection.AsOf(TxId.From(txId.Value));
            var oldMetadata = Sdk.Games.GameInstallMetadata.Load(asOfDb, metadata.Id);
            prevItems = GetDiskStateForGame(oldMetadata);
        }
        return prevItems;
    }
    
    /// <summary>
    /// Gets the previously applied disk state for a game.
    /// </summary>
    public List<PathPartPair> GetLastScannedDiskState(Sdk.Games.GameInstallMetadata.ReadOnly metadata)
    {
        List<PathPartPair> prevItems;
        if (!Sdk.Games.GameInstallMetadata.LastScannedDiskStateTransaction.TryGetValue(metadata, out var txId))
        {
            prevItems = [];
        }
        else
        {
            var asOfDb = metadata.Db.Connection.AsOf(TxId.From(txId.Value));
            var oldMetadata = Sdk.Games.GameInstallMetadata.Load(asOfDb, metadata.Id);
            prevItems = GetDiskStateForGame(oldMetadata);
        }
        return prevItems;
    }
    
    /// <summary>
    /// Get the disk state for a game from the given database.
    /// </summary>
    public List<PathPartPair> GetDiskStateForGame(Sdk.Games.GameInstallMetadata.ReadOnly metadata);

    public bool ShouldSynchronize(Loadout.ReadOnly loadout, IEnumerable<PathPartPair> previousState, IEnumerable<PathPartPair> lastScannedState);
    
    /// <summary>
    /// Computes the difference between a loadout and a disk state, assuming the loadout to be the newer state.
    /// </summary>x
    /// <param name="loadout">Newer state, e.g. unapplied loadout</param>
    /// <param name="previousState">The old state, e.g. last applied DiskState</param>
    /// <param name="lastScannedState">The last scanned state, e.g. the last time the game folder was scanned</param>
    /// <returns>A tree of all the files with associated <see cref="FileChangeType"/></returns>
    FileDiffTree LoadoutToDiskDiff(Loadout.ReadOnly loadout, List<PathPartPair> previousState, List<PathPartPair> lastScannedState);

    Task<Sdk.Games.GameInstallMetadata.ReadOnly> ReindexState(GameInstallation installation, SynchronizeLoadoutJob? job = null);
    ValueTask BuildProcessRun(Loadout.ReadOnly loadout, Sdk.Games.GameInstallMetadata.ReadOnly state, CancellationToken cancellationToken);

    /// <summary>
    /// Resets the game folder to the vanilla file list of <paramref name="locatorIds"/>, deleting every indexed file
    /// outside it. Throws <see cref="SynchronizerPreflightException"/> without touching anything when
    /// <see cref="EnsureBuildCovered"/> does.
    /// </summary>
    Task ResetToOriginalGameState(GameInstallation installation, LocatorId[] locatorIds);

    /// <summary>
    /// Throws <see cref="SynchronizerPreflightException"/> when the file hashes data does not describe every part of
    /// the build with these locator ids. The vanilla file list is all that tells the game's own files from added
    /// ones, so anything that deletes files by it (a reset, cleaning the folder) must pass this first.
    /// </summary>
    void EnsureBuildCovered(GameInstallation installation, LocatorId[] locatorIds);

    /// <summary>
    /// Returns true if the path should be ignored by the synchronizer when backing up or restoring files. This does not mean
    /// that files on the given path will not be managed or moddable, just that the default files on that path are not backed
    /// up. This method is ignored when the global configuration is set to always back up all game files.
    /// </summary>
    bool IsIgnoredBackupPath(GamePath path);

    /// <summary>
    /// Returns true if the game writes this file itself while it runs (a log, a crash dump), so it is never a mod
    /// or an edit. This is not <see cref="IsIgnoredBackupPath"/>: that says what is not worth backing up, which
    /// for some games is the whole install folder, mods included. By default logs and crash dumps count, see
    /// <see cref="GameWrittenFiles"/>.
    /// </summary>
    bool IsGameWrittenPath(GamePath path);
}

public record struct FileConflictGroup(GamePath Path, FileConflictItem[] Items);
public record struct FileConflictItem(bool IsEnabled, OneOf<LoadoutFile.ReadOnly, DeletedFile.ReadOnly> File);

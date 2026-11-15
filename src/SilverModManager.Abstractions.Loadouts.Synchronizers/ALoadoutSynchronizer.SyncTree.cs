using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.GeneratedCaches;
using SilverModManager.Abstractions.Loadouts.Sorting;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.ForeignDeployments;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Reloaded.Memory.Extensions;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

using DiskState = Entities<DiskStateEntry.ReadOnly>;

public partial class ALoadoutSynchronizer
{
    private class LoadoutItemGroupComparer : IEqualityComparer<LoadoutItemGroup.ReadOnly>, IAlternateEqualityComparer<EntityId, LoadoutItemGroup.ReadOnly>
    {
        public static readonly IEqualityComparer<LoadoutItemGroup.ReadOnly> Instance = new LoadoutItemGroupComparer();

        public bool Equals(LoadoutItemGroup.ReadOnly x, LoadoutItemGroup.ReadOnly y) => x.Id.Equals(y.Id);
        public int GetHashCode(LoadoutItemGroup.ReadOnly item) => item.Id.GetHashCode();
        public bool Equals(EntityId alternate, LoadoutItemGroup.ReadOnly other) => other.Id.Equals(alternate);
        public int GetHashCode(EntityId alternate) => alternate.GetHashCode();
        public LoadoutItemGroup.ReadOnly Create(EntityId alternate) => throw new NotSupportedException();
    }

    public Dictionary<GamePath, SyncNode> BuildSyncTree<T>(T latestDiskState, T previousDiskState, Loadout.ReadOnly loadout) where T : IEnumerable<PathPartPair>
    {
        Dictionary<GamePath, SyncNode> syncTree = new();

        // The loadout's own revision, not the latest one: a commit that lands after the caller took its loadout would
        // otherwise put files in the tree that the caller's loadout (which games check the tree against) never listed.
        foreach (var tuple in WinningFilesQuery(loadout.Db, loadout))
        {
            var itemType = ToItemType(tuple.ItemType);

            // NOTE(erri120): deleted files are not added to the sync tree
            if (itemType == LoadoutSourceItemType.Deleted) continue;

            var gamePath = new GamePath(tuple.Location, tuple.Path);
            if (gamePath == default(GamePath)) throw new Exception($"Item of type `{itemType}` with ID `{tuple.Id}` has no valid game path!");

            // The disk side of the tree never contains filtered paths, so a loadout item on one has to be left
            // out too: otherwise the sync would write the file and could never take it off disk again. A mod
            // flagged to allow protected paths is the exception, its files are tracked on disk once deployed.
            if (GamePathFilter.ShouldFilter(gamePath) && IsSkippedAsProtected(LoadoutItem.Load(loadout.Db, tuple.Id), gamePath))
            {
                // The store's own record of a file the game rewrites is skipped every time; only a mod's file is news.
                if (itemType != LoadoutSourceItemType.Game) LogSkippedFilteredItem(loadout.Db, tuple.Id, gamePath);
                continue;
            }

            ref var syncTreeEntry = ref CollectionsMarshal.GetValueRefOrAddDefault(syncTree, gamePath, out var exists);
            Debug.Assert(!exists, "query should not return duplicate items");

            if (exists)
            {
                Logger.LogWarning("Duplicate file for `{Path}`: {Item}", gamePath, tuple);
                continue;
            }

            var loadoutPart = itemType switch
            {
                LoadoutSourceItemType.Game => new SyncNodePart
                {
                    Identity = ParseIdentity(tuple.Identity),
                    Size = tuple.Size,
                    LastModifiedTicks = 0,
                },
                LoadoutSourceItemType.Loadout => new SyncNodePart
                {
                    EntityId = tuple.Id,
                    Identity = ParseIdentity(tuple.Identity),
                    Size = tuple.Size,
                    LastModifiedTicks = 0,
                },
                LoadoutSourceItemType.Intrinsic => default(SyncNodePart),
                LoadoutSourceItemType.Deleted => throw new UnreachableException("Deleted files should've been filtered out"),
            };

            syncTreeEntry = new SyncNode
            {
                SourceItemType = itemType,
                Loadout = loadoutPart,
            };
        }

        var manifests = MountManifests(loadout.Installation.Id);
        MergeStates(OverlayMountManifests(manifests, latestDiskState), OverlayMountManifests(manifests, previousDiskState), syncTree);
        MarkPlayerDeletions(syncTree, loadout);
        TreatRetractedModFilesAsNew(syncTree, loadout);
        var resolver = _identityResolver.Scoped(IdentitiesToResolve(syncTree));
        NameTheSameBytesByOneKind(syncTree, resolver);
        MarkGameOwnBytesTheLoadoutNoLongerNames(syncTree, loadout, resolver);
        AdoptGameInstalledFiles(syncTree, loadout);
        LeaveUserProtectedFilesAlone(syncTree, loadout);
        return syncTree;
    }

    /// <summary>
    /// Flags the nodes the player deleted from disk, when the row that provides the file is
    /// the player's and not a mod's, so there is nothing to restore it from and nothing to re-deploy:
    /// <list type="bullet">
    /// <item>An Overrides row, after an earlier apply: adopted from the game folder (its bytes may never have been copied into the store) or
    /// kept from an edit. A mod that provides the same path underneath is not consulted: the Overrides row is dropped
    /// and the mod's copy comes back on the next sync.</item>
    /// <item>A Game row the store's file list does not name. With no depot manifest for the install, the baseline
    /// recorded whatever was on disk, old mod files included, and only the disk vouched for them. A path the list
    /// does name stays a warning: Steam can put it back. The file need not have been seen by an earlier apply.</item>
    /// </list>
    /// </summary>
    private void MarkPlayerDeletions(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly loadout)
    {
        EntityId? overridesId = null;
        Dictionary<string, GameFileRecord>? listed = null;
        foreach (var path in tree.Keys.ToArray())
        {
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(tree, path);
            if (node.HaveDisk) continue;

            switch (node.SourceItemType)
            {
                // An Overrides row with no previous entry is a file that was never on disk, such as one still waiting
                // to be deployed, so it stays in the list.
                case LoadoutSourceItemType.Loadout when node.HavePrevious && node.Loadout.HaveEntityId:
                    overridesId ??= OverridesGroupLookup.Find(loadout);
                    if (overridesId.Value.Value == 0) continue;
                    node.PlayerDeleted = OverridesGroupLookup.IsInGroup(loadout.Db, node.Loadout.EntityId, overridesId.Value);
                    break;
                // A Game row needs no previous entry: the file can be gone before any apply recorded it, and the
                // baseline that named it is the only thing that ever saw it.
                case LoadoutSourceItemType.Game:
                    listed ??= ListedGameFiles(loadout.InstallationInstance);
                    node.PlayerDeleted = !listed.ContainsKey(path.Path.ToString());
                    break;
            }
        }
    }

    /// <summary>
    /// A file on disk and in the previous state that no loadout row names any more, and that the baseline says looks
    /// like a mod, was a snapshot Game row until the baseline retracted it. The loadout never deployed it, so it is
    /// not a mod the loadout dropped and must not be backed up and deleted: the previous side is cleared, which makes
    /// it a new file on disk that the sync ingests into Overrides, as the first sync does for such files.
    /// </summary>
    private void TreatRetractedModFilesAsNew(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly loadout)
    {
        IReadOnlySet<GamePath>? modShaped = null;
        foreach (var path in tree.Keys.ToArray())
        {
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(tree, path);
            if (node.HaveLoadout || !node.HaveDisk || !node.HavePrevious) continue;

            modShaped ??= _fileHashService.FilesThatLookLikeMods(loadout.InstallationInstance);
            if (!modShaped.Contains(path)) continue;

            _perFileLogger.LogTrace("`{Path}` is on disk and was applied, but the baseline retracted it as looking like a mod: treating it as a new file", path);
            node.Previous = default;
        }
    }

    /// <summary>
    /// Flags a file that is on disk and in the previous state, has no loadout row, and holds bytes known to differ
    /// from the previous ones when those bytes are the game's own. An earlier apply replaced the game's file (a mod,
    /// or a Wabbajack list), the store restored it, and the row that stood for the vanilla file is gone because the
    /// baseline had called the path non-vanilla while the replacement was on disk. Without the flag the generic rules
    /// read it as a removed mod's file that was changed since and delete it. A file whose identity cannot be compared
    /// yet is not flagged: it may be the mod's own file, unchanged since the apply. The initial disk state and the
    /// store's list are loaded once, and only if some node gets that far.
    /// </summary>
    private void MarkGameOwnBytesTheLoadoutNoLongerNames(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly loadout, IFileIdentityResolver resolver)
    {
        Dictionary<GamePath, SyncNodePart>? initial = null;
        Dictionary<string, GameFileRecord>? listed = null;
        foreach (var path in tree.Keys.ToArray())
        {
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(tree, path);
            if (node.HaveLoadout || !node.HaveDisk || !node.HavePrevious) continue;
            if (MatchParts(resolver, true, node.Disk, true, node.Previous) != IdentityMatch.Different) continue;

            initial ??= InitialDiskState(loadout.InstallationId);
            node.DiskIsGameOwn = IsGameOwnBytes(resolver, loadout.InstallationInstance, path, node.Disk, diskDiffersFromPrevious: true, initial, ref listed);
        }
    }

    /// <summary>
    /// The store's file list for the installed build (depot manifests of its locator ids plus the baseline's attested
    /// rows), by path. A Snapshot baseline contributes nothing to it.
    /// </summary>
    private Dictionary<string, GameFileRecord> ListedGameFiles(GameInstallation installation)
    {
        var listed = new Dictionary<string, GameFileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _fileHashService.GetGameFiles(installation))
        {
            if (file.Path.LocationId == LocationId.Game) listed[file.Path.Path.ToString()] = file;
        }
        return listed;
    }

    /// <summary>
    /// A file a loadout row names but the store holds no copy of cannot be written to disk. When the file is gone
    /// from disk and the row is the player's (see <see cref="MarkPlayerDeletions"/>), that is the player deleting it,
    /// so the row is dropped instead of waiting for an archive that never existed. A mod's file stays a warning: its
    /// archive is the library's to restore.
    /// </summary>
    private static void TakePlayerDeletion(ref SyncNode node)
    {
        if (node.PlayerDeleted && node.Actions == Actions.WarnOfUnableToExtract)
            node.Actions = Actions.AddReifiedDelete;
    }

    /// <summary>
    /// A file the store vouched for is named by its SHA-1 until the app reads it, and a read names it by its xxHash3.
    /// The two never compare equal, so a path whose parts are named by different kinds would look changed even when a
    /// full read recorded that both are the same bytes. Where a recorded relation joins a SHA-1 to the xxHash3 of
    /// another part of the same path, the SHA-1 part is renamed to that xxHash3 for this tree. A SHA-1 no relation
    /// joins stays as it is: nothing says those bytes are the same.
    /// </summary>
    private void NameTheSameBytesByOneKind(Dictionary<GamePath, SyncNode> tree, IFileIdentityResolver resolver)
    {
        List<GamePath>? mixed = null;
        foreach (var (path, node) in tree)
        {
            if (HasMixedKinds(node)) (mixed ??= []).Add(path);
        }

        if (mixed is null) return;
        foreach (var path in mixed)
        {
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(tree, path);
            var disk = node.Disk;
            var previous = node.Previous;
            var loadout = node.Loadout;
            RenameBySharedRelation(resolver, path, ref disk, previous, loadout);
            RenameBySharedRelation(resolver, path, ref previous, disk, loadout);
            RenameBySharedRelation(resolver, path, ref loadout, disk, previous);
            node.Disk = disk;
            node.Previous = previous;
            node.Loadout = loadout;
        }
    }

    /// <summary>
    /// The identities a pass over <paramref name="tree"/> may ask the recorded digests about: every part of a node whose
    /// parts are named by different kinds, and any SHA-1 (whose archive key is a recorded digest). Handing them to the
    /// resolver up front lets it answer for all of them with one query per hash kind.
    /// </summary>
    private static IEnumerable<FileIdentity> IdentitiesToResolve(Dictionary<GamePath, SyncNode> tree)
    {
        foreach (var node in tree.Values)
        {
            var mixed = HasMixedKinds(node);
            if (Asked(node.Disk.Identity, mixed)) yield return node.Disk.Identity;
            // Most nodes carry the same identity on disk and in the previous state; the resolver sorts and dedupes, but
            // handing it the repeat only to throw it away costs a list slot per node.
            if (Asked(node.Previous.Identity, mixed) && !node.Previous.Identity.Equals(node.Disk.Identity)) yield return node.Previous.Identity;
            if (Asked(node.Loadout.Identity, mixed)) yield return node.Loadout.Identity;
        }

        static bool Asked(FileIdentity identity, bool mixed) => !identity.IsNone && (mixed || identity.Kind == HashKind.Sha1);
    }

    private static bool HasMixedKinds(SyncNode node)
    {
        var kind = HashKind.None;
        return Differs(ref kind, node.Disk) || Differs(ref kind, node.Previous) || Differs(ref kind, node.Loadout);

        static bool Differs(ref HashKind seen, SyncNodePart part)
        {
            if (part.Identity.IsNone) return false;
            if (seen == HashKind.None) seen = part.Identity.Kind;
            return seen != part.Identity.Kind;
        }
    }

    private void RenameBySharedRelation(IFileIdentityResolver resolver, GamePath path, ref SyncNodePart part, SyncNodePart first, SyncNodePart second)
    {
        if (part.Identity.Kind != HashKind.Sha1) return;
        if (JoinsByRelation(resolver, part, first)) { part = part with { Identity = first.Identity }; return; }
        if (JoinsByRelation(resolver, part, second)) { part = part with { Identity = second.Identity }; return; }

        // Left as a SHA-1 beside an xxHash3 part: the pair stays mixed and reads as changed.
        if (!_perFileLogger.IsEnabled(LogLevel.Trace)) return;
        TraceUnjoined(resolver, path, part, first);
        TraceUnjoined(resolver, path, part, second);
    }

    private static bool JoinsByRelation(IFileIdentityResolver resolver, SyncNodePart sha1Part, SyncNodePart other)
        => other.Identity.Kind == HashKind.XxHash3 && resolver.Resolve(sha1Part.Identity, other.Identity, sha1Part.Size) == IdentityMatch.Same;

    private void TraceUnjoined(IFileIdentityResolver resolver, GamePath path, SyncNodePart part, SyncNodePart other)
    {
        if (other.Identity.IsNone || other.Identity.Kind == HashKind.Sha1) return;
        if (resolver.Resolve(part.Identity, other.Identity, part.Size) == IdentityMatch.Different)
        {
            _perFileLogger.LogTrace("The relations of {Size} bytes name other bytes than {Identity} for the {OtherKind} part {OtherIdentity} of `{Path}`",
                part.Size, part.Identity, other.Identity.Kind, other.Identity, path);
        }
        else
        {
            _perFileLogger.LogTrace("No relation of {Size} bytes joins the {OtherKind} part {OtherIdentity} of `{Path}` to {Identity}; searched {Databases} hash database(s)",
                part.Size, other.Identity.Kind, other.Identity, path, part.Identity, _fileHashService.Databases.Count);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Shared Proton caches and files no game loads are skipped even for a flagged item: the flag exists for save
    /// files, a cache changes on every launch, and nothing makes a source file loadable.
    /// </remarks>
    public bool IsSkippedAsProtected(LoadoutItem.ReadOnly item, GamePath path)
    {
        if (!GamePathFilter.ShouldFilter(path)) return false;
        if (IsSharedProtonCache(path) || NeverLoadedFiles.IsNeverLoaded(path)) return true;

        while (item.IsValid())
        {
            if (item.IsProtectedPathsAllowed) return false;
            if (!item.HasParent()) return true;
            item = item.Parent.AsLoadoutItem();
        }

        return true;
    }

    /// <summary>
    /// Takes files on protected paths out of the tree when deploying or deleting them would touch something
    /// that is not the loadout's to touch.
    /// </summary>
    /// <remarks>
    /// Protected paths are not indexed unless the loadout deployed to them, so a file there that the disk
    /// state does not know is the user's own and is never overwritten. A deployed file that no longer matches the
    /// loadout's copy is the user's too (the game rewrites saves as it runs): it must be neither ingested into
    /// the loadout nor overwritten with the mod's version. When there is a previous state to compare against,
    /// <see cref="ProcessSyncTree"/> makes that call instead, since it can also tell an untouched file from a
    /// rewritten one when the mod has been updated.
    /// </remarks>
    private void LeaveUserProtectedFilesAlone(Dictionary<GamePath, SyncNode> syncTree, Loadout.ReadOnly loadout)
    {
        List<GamePath>? toRemove = null;

        foreach (var (path, node) in syncTree)
        {
            if (!node.HaveLoadout || !GamePathFilter.ShouldFilter(path)) continue;

            if (node.HaveLoadout && !node.HaveDisk && !node.HavePrevious)
            {
                var onDisk = loadout.InstallationInstance.Locations.ToAbsolutePath(path);
                if (!onDisk.FileExists) continue;
                Logger.LogWarning("Not deploying `{Path}`: a file of that name is already there and was not put there by the loadout", path);
            }
            else if (node.HaveDisk && !node.HavePrevious && node.Disk.Identity.MatchSameKind(node.Loadout.Identity) != IdentityMatch.Same)
            {
                Logger.LogWarning("Leaving `{Path}` alone: it differs from the loadout's copy, so the game or the user has changed it since", path);
            }
            else
            {
                continue;
            }

            (toRemove ??= []).Add(path);
        }

        if (toRemove is null) return;
        foreach (var path in toRemove) syncTree.Remove(path);
    }

    private void LogSkippedFilteredItem(IDb db, EntityId itemId, GamePath gamePath)
    {
        var item = LoadoutItem.Load(db, itemId);
        var group = item.IsValid() && item.HasParent() ? item.Parent.AsLoadoutItem().Name : "(no group)";
        // Vortex puts a marker in every folder it deploys to, so one per mod folder is routine, not something gone wrong.
        var level = _foreignDeploymentSources.Any(source => source.IsFolderMarker(gamePath)) ? LogLevel.Debug : LogLevel.Warning;
        Logger.Log(level, "Skipping `{Path}` from `{Group}`: the synchronizer ignores this path", gamePath, group);
    }

    /// <summary>
    /// The manifest of every root the plan routes to a mounting backend. A mounting backend writes nothing under its
    /// root, so a sync tree built without them would compare the loadout with files that are not there.
    /// </summary>
    private IReadOnlyList<MountManifestView> MountManifests(GameInstallMetadataId id)
    {
        var db = Connection.Db;
        List<MountManifestView>? views = null;
        foreach (var root in _planStore.Load(db, id).Roots)
        {
            if (_backends.GetValueOrDefault(root.Backend) is not IMountingDeploymentBackend mounting) continue;
            (views ??= []).Add(mounting.Manifest(id, root.Root, db));
        }
        return views ?? [];
    }

    /// <summary>
    /// Lays <paramref name="manifests"/> over <paramref name="state"/>.
    /// </summary>
    /// <remarks>
    /// Only for state that goes into a sync tree. The state handed to the index is what is really on disk: a row whose
    /// file is not there would read as a removed file.
    /// </remarks>
    private static IEnumerable<PathPartPair> OverlayMountManifests(IReadOnlyList<MountManifestView> manifests, IEnumerable<PathPartPair> state)
    {
        foreach (var manifest in manifests) state = manifest.Overlay(state);
        return state;
    }

    /// <inheritdoc />
    public void MergeStates(IEnumerable<PathPartPair> currentState, IEnumerable<PathPartPair> previousTree, Dictionary<GamePath, SyncNode> loadoutItems)
    {
        // Disk before the previous state: paths compare without regard to case, so the spelling a node is keyed by is the
        // first one added, and a delete must name the file as it is on disk now, not as an earlier sync recorded it. The
        // loadout's rows were added before either, so a row spelled differently from the disk keys its node, and the
        // folder-case fold rewrites the rows to the disk's spelling before the tree is built.
        foreach (var node in currentState)
        {
            ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(loadoutItems, node.Path, out var exists);
            if (exists)
            {
                existing.Disk = node.Part;
            }
            else
            {
                existing = new SyncNode
                {
                    Disk = node.Part,
                };
            }
        }

        foreach (var node in previousTree)
        {
            ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(loadoutItems, node.Path, out var exists);
            if (exists)
            {
                existing.Previous = node.Part;
            }
            else
            {
                existing = new SyncNode
                {
                    Previous = node.Part,
                };
            }
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<GamePath, SyncNode>> BuildSyncTree(Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job = null)
    {
        var metadata = await ReindexState(loadout.InstallationInstance, job);
        return BuildSyncTreeFromIndex(loadout, metadata);
    }

    /// <summary>
    /// Builds the tree from a disk state that is already indexed. Split from the public overload so an Apply can
    /// reconcile and prepare the loadout between the reindex and the tree.
    /// </summary>
    private Dictionary<GamePath, SyncNode> BuildSyncTreeFromIndex(Loadout.ReadOnly loadout, GameInstallMetadata.ReadOnly metadata)
    {
        var currentItems = GetDiskStateForGame(metadata);
        var prevItems = ForgetRelocatedFiles(((ILoadoutSynchronizer)this).GetPreviouslyAppliedDiskState(metadata), currentItems);

        return BuildSyncTree(currentItems, prevItems, loadout);
    }

    /// <summary>
    /// Locations whose folder can change under an installation between two syncs, for example when a game that was
    /// found in a Proton prefix is found as its native build. Nothing the loadout deployed there is on disk any more
    /// when that happens, which would read as the user deleting every one of those files.
    /// </summary>
    protected virtual IReadOnlySet<LocationId> RelocatableLocations => FrozenSet<LocationId>.Empty;

    /// <summary>
    /// Takes the previously applied files of a <see cref="RelocatableLocations">relocatable</see> location out of the
    /// previous state when none of them is on disk. The loadout then deploys them again; nothing is deleted.
    /// A location with even one of its files still on disk is not treated as moved, so a file the user deleted
    /// stays deleted.
    /// </summary>
    private List<PathPartPair> ForgetRelocatedFiles(List<PathPartPair> previous, List<PathPartPair> current)
    {
        var relocatable = RelocatableLocations;
        if (relocatable.Count == 0 || previous.Count == 0) return previous;

        var onDisk = current.Select(pair => pair.Path).ToHashSet();
        var moved = relocatable
            .Where(location => previous.Any(pair => pair.Path.LocationId == location)
                               && !previous.Any(pair => pair.Path.LocationId == location && onDisk.Contains(pair.Path)))
            .ToHashSet();
        if (moved.Count == 0) return previous;

        Logger.LogInformation("None of the files last deployed to {Locations} are on disk, so the folder is taken to have moved and they will be deployed again", string.Join(", ", moved));
        return previous.Where(pair => !moved.Contains(pair.Path.LocationId)).ToList();
    }
}

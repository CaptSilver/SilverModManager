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
    /// <summary>
    /// Lets a game change what the generic mapping decided, once every node's <see cref="SyncNode.Actions"/> is
    /// set. A game uses it to keep a loadout file off disk without that reading as the user deleting it.
    /// </summary>
    /// <remarks>
    /// <see cref="ProcessSyncTree"/> also runs for <see cref="ShouldSynchronize"/> and
    /// <see cref="LoadoutToDiskDiff"/>, which have no loadout in hand and run on every refresh, so an override
    /// has to stay cheap and must not read file bytes. Assign a node's actions as a whole rather than clearing
    /// flags: a node left with no action must be <see cref="Actions.DoNothing"/> (bit 0), because a node whose
    /// flags were merely cleared is 0, which <see cref="ShouldSynchronize"/> counts as pending work forever.
    /// Nodes for intrinsic files are added by <see cref="BuildSyncTree{T}"/> with <c>Loadout</c> left at its
    /// default and a <see cref="SyncNode.SourceItemType"/> of <see cref="LoadoutSourceItemType.Intrinsic"/>;
    /// an override can tell them from loadout files by that.
    /// </remarks>
    protected virtual void AdjustActions(Dictionary<GamePath, SyncNode> tree) { }

    /// <inheritdoc />
    public void ProcessSyncTree(Dictionary<GamePath, SyncNode> tree)
    {
        var resolver = _identityResolver.Scoped(IdentitiesToResolve(tree));
        foreach (var path in tree.Keys)
        {
            // TODO: sucks that we have to do a lookup here, but we have no way to get the ref to the value otherwise
            ref var item = ref CollectionsMarshal.GetValueRefOrNullRef(tree, path);
            MapActions(path, ref item, resolver);
        }

        AdjustActions(tree);
    }

    /// <summary>
    /// Two kinds with no join are unknown, unless the recorded digests of one side's bytes name other bytes than the
    /// other side: that is a known difference, and reading the file would only repeat it.
    /// </summary>
    private static IdentityMatch MatchParts(IFileIdentityResolver resolver, bool haveLeft, SyncNodePart left, bool haveRight, SyncNodePart right)
    {
        if (!haveLeft || !haveRight) return IdentityMatch.Different;

        var match = left.Identity.MatchSameKind(right.Identity);
        return match == IdentityMatch.Unknown && resolver.Resolve(left.Identity, right.Identity, left.Size) == IdentityMatch.Different
            ? IdentityMatch.Different
            : match;
    }

    /// <summary>
    /// Signs one node and gives it the actions that signature maps to. The resolver is scoped to the pass, so a node's
    /// three pairs put each question to the recorded digests once.
    /// </summary>
    private void MapActions(GamePath path, ref SyncNode item, IFileIdentityResolver resolver)
    {
        var diskPrev = MatchParts(resolver, item.HaveDisk, item.Disk, item.HavePrevious, item.Previous);
        var prevLoadout = MatchParts(resolver, item.HavePrevious, item.Previous, item.HaveLoadout, item.Loadout);
        var diskLoadout = MatchParts(resolver, item.HaveDisk, item.Disk, item.HaveLoadout, item.Loadout);
        item.IdentityUnresolved = diskPrev == IdentityMatch.Unknown || prevLoadout == IdentityMatch.Unknown || diskLoadout == IdentityMatch.Unknown;

        var signature = SignatureBuilder.Build(
            diskExists: item.HaveDisk,
            prevExists: item.HavePrevious,
            loadoutExists: item.HaveLoadout,
            diskPrev: diskPrev,
            prevLoadout: prevLoadout,
            diskLoadout: diskLoadout,
            diskArchived: item.HaveDisk && HaveArchive(resolver, item.Disk.Identity, item.Disk.Size),
            prevArchived: item.HavePrevious && HaveArchive(resolver, item.Previous.Identity, item.Previous.Size),
            loadoutArchived: item.HaveLoadout && HaveArchive(resolver, item.Loadout.Identity, item.Loadout.Size),
            pathIsIgnored: IsIgnoredBackupPath(path),
            item.SourceItemType);

        item.Signature = signature;
        item.Actions = ActionMapping.MapActions(signature);
        TakePlayerDeletion(ref item);

        // A deployed file on a protected path that no longer matches what the last sync wrote has been
        // rewritten by the game. It is the player's now: whether or not the loadout still provides it, the
        // generic rules would delete it or write the mod's copy over it. Dropping the disk state entry also
        // keeps the verdict: once the entry's hash follows the rewrite, the rewrite would look like a change
        // the loadout has not caught up with.
        if (item.HaveDisk && item.HavePrevious && item.Disk.Identity.MatchSameKind(item.Previous.Identity) != IdentityMatch.Same && GamePathFilter.ShouldFilter(path))
            item.Actions = Actions.ForgetDiskState;

        LeaveGameWrittenFileAlone(path, ref item);
        LeaveUnappliedNeverLoadedFileAlone(path, ref item);
        LeaveGameOwnBytesAlone(ref item);
    }

    /// <summary>
    /// A file the store put back over what an earlier apply wrote (see <see cref="SyncNode.DiskIsGameOwn"/>) is not a
    /// removed mod's file. Its disk state entry is dropped and the bytes stay, so nothing is deleted. With the entry
    /// gone the next sync finds a file on disk that nothing tracks and ingests it like any other: the baseline repair,
    /// not this, is what puts the loadout's own row for the game's file back.
    /// </summary>
    private static void LeaveGameOwnBytesAlone(ref SyncNode item)
    {
        // A flag left from before a read settled the node's identity does not hold once the disk equals the previous bytes.
        if (!item.DiskIsGameOwn || item.Signature.HasFlag(Signature.DiskEqualsPrev) || !item.Actions.HasFlag(Actions.DeleteFromDisk)) return;

        item.Actions = Actions.ForgetDiskState;
    }

    /// <summary>
    /// A source or project file (see <see cref="NeverLoadedFiles"/>) that is indexed but absent from the last applied
    /// state was not put there by an apply, so it is not the loadout's to ingest, and the generic rules would take it
    /// in as an external change. Tracking stops and the file stays. One that the last apply did record has an
    /// older build's mod behind it and takes the generic path: with no loadout side it is removed like any other
    /// file of a mod that is gone.
    /// </summary>
    private static void LeaveUnappliedNeverLoadedFileAlone(GamePath path, ref SyncNode item)
    {
        if (item.HaveLoadout || !item.HaveDisk || item.HavePrevious || !NeverLoadedFiles.IsNeverLoaded(path)) return;

        item.Actions = Actions.ForgetDiskState;
    }

    /// <summary>
    /// A filtered path the game names as its own output (a log, a crash dump, a file it rebuilds) is never the
    /// loadout's to write or remove. It can still be tracked: an earlier build indexed it, or its Overrides row was
    /// just dropped, and with no loadout side the generic rules would back it up and delete it (unchanged since the
    /// last apply) or ingest it as an override (new since then). Stop tracking it and leave it on disk. A mod file
    /// allowed on the path has a loadout-typed node and goes through the generic rules; a game-typed loadout side
    /// (a reset's vanilla copy) is not the player's and is left alone too.
    /// </summary>
    private void LeaveGameWrittenFileAlone(GamePath path, ref SyncNode item)
    {
        if (item.HaveLoadout && item.SourceItemType != LoadoutSourceItemType.Game) return;
        if (!IsGameOrCacheOutput(path) || !GamePathFilter.ShouldFilter(path)) return;

        item.Actions = item.HaveDisk ? Actions.ForgetDiskState : Actions.DoNothing;
    }
}

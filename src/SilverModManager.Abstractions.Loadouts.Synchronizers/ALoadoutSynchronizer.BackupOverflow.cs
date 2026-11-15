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
    /// Measures what the tree would back up and, when it is over <see cref="BackupLimit"/>, settles the question of
    /// what to do about it. Runs before <see cref="_lock"/> is taken, so a question that waits on a person holds up
    /// nothing but the callers queued behind the synchronizer service.
    /// </summary>
    /// <remarks>
    /// Public so the check can be driven with a hand-built tree. A <see cref="BackupOverflowChoice.Refuse"/>
    /// <paramref name="preset"/> throws without asking, which is what the command line relies on. <c>Asked</c> is
    /// true when the prompt answered, so the caller knows the folder may have changed while it was open.
    /// </remarks>
    public async Task<(BackupOverflowChoice Choice, bool Asked)> ResolveBackupOverflow(
        Dictionary<GamePath, SyncNode> syncTree,
        GameInstallation installation,
        BackupOverflowChoice? preset,
        CancellationToken cancellationToken,
        bool presetIsAnAnswer = false,
        bool adoptOnlyBacksUp = false,
        BackupOverflowOperation operation = BackupOverflowOperation.Apply,
        SynchronizeLoadoutJob? job = null)
    {
        // A file whose parts no recorded digest compares is read first: counting it would ask about a file that may be unchanged.
        await ResolveUnresolvedIdentities(syncTree, installation, cancellationToken, job);

        // The operation the player started names the question; the leg only knows its own step.
        var overflow = await MeasureBackupOverflow(syncTree, installation, BackupOverflowAnswer.Active?.Operation ?? operation, cancellationToken);
        if (overflow is null) return (BackupOverflowChoice.BackUpAnyway, false);

        if (preset == BackupOverflowChoice.Refuse) throw new BackupOverflowException(installation, overflow);

        // An earlier step of the same operation has put the question already.
        var scope = BackupOverflowAnswer.Active;
        if (preset is null && scope?.Remembered is { } carried)
        {
            preset = carried;
            presetIsAnAnswer = true;
        }

        // Every overflow node of a reset is deleted after its copy, so there is nothing to leave in place: adopting
        // can only mean backing up, which the player's earlier adopt answer already accepted for the other files.
        if (adoptOnlyBacksUp && preset == BackupOverflowChoice.AdoptInPlace) preset = BackupOverflowChoice.BackUpAnyway;

        BackupOverflowChoice choice;
        var asked = preset is null;
        if (preset is { } given && !(presetIsAnAnswer && given == BackupOverflowChoice.AdoptInPlace && !overflow.CanAdopt))
        {
            choice = given;
        }
        else
        {
            // Either no answer yet, or the folder changed since the player gave theirs and adopting would now lose files.
            asked = true;

            // The sidebar would otherwise keep showing the finished "Collecting files (n/n)" for as long as a person
            // takes to answer.
            job?.SetStatus(SyncPhases.WaitingForAnswer);
            try
            {
                choice = await _backupOverflowPrompt.Ask(installation, overflow, cancellationToken);
            }
            finally
            {
                job?.SetStatus(SyncPhases.Collecting);
            }

            if (choice == BackupOverflowChoice.Refuse) throw new BackupOverflowException(installation, overflow) { Declined = true };
            scope?.Remember(choice);
        }

        if (choice == BackupOverflowChoice.AdoptInPlace && !overflow.CanAdopt)
            throw BackupOverflowException.AdoptWouldLoseFiles(installation, overflow);

        Logger.LogInformation(
            "{FileCount} files ({Total}) to back up are over the {Limit} cap, {Choice} ({Adoptable} of them only want a copy, {MustBackUp} must be backed up)",
            overflow.FileCount, overflow.TotalText, overflow.LimitText, choice, overflow.AdoptableText, overflow.MustBackUpText);
        return (choice, asked);
    }

    /// <summary>
    /// The size of every file the tree would back up that the file store does not already hold, split into the
    /// files that stay on disk and only want a copy and the ones that must be archived. Null when the total fits.
    /// </summary>
    private async Task<BackupOverflow?> MeasureBackupOverflow(Dictionary<GamePath, SyncNode> syncTree, GameInstallation installation, BackupOverflowOperation operation, CancellationToken cancellationToken)
    {
        // A path the synchronizer never writes loses its backup in RunActions; counting it here would ask about
        // files that are never copied.
        KeepNeverWrittenPathsOffDisk(syncTree);

        long adoptable = 0, mustBackUp = 0;
        var adoptableCount = 0;
        var mustBackUpCount = 0;
        var measured = new ConcurrentBag<MeasuredFile>();
        await Parallel.ForEachAsync(syncTree, new ParallelOptions { MaxDegreeOfParallelism = HashingLimits.FileReaders, CancellationToken = cancellationToken }, async (item, _) =>
        {
            var node = item.Value;
            if (!node.Actions.HasFlag(Actions.BackupFile)) return;
            if (await HaveArchiveAsync(node.Disk.Identity, node.Disk.Size)) return;

            if (node.Actions == (Actions.BackupFile | Actions.IngestFromDisk))
            {
                Interlocked.Add(ref adoptable, (long)node.Disk.Size.Value);
                Interlocked.Increment(ref adoptableCount);
            }
            else
            {
                Interlocked.Add(ref mustBackUp, (long)node.Disk.Size.Value);
                Interlocked.Increment(ref mustBackUpCount);
            }

            measured.Add(new MeasuredFile(item.Key, node.Disk.Size.Value, KindOf(node), node.Disk.Identity));
        });

        var overflow = new BackupOverflow(Size.FromLong(adoptable), adoptableCount, Size.FromLong(mustBackUp), mustBackUpCount, _backupLimit.Maximum, operation);
        if (overflow.Total <= overflow.Limit) return null;

        var counted = TellGameFilesApart(measured, installation);

        return overflow with
        {
            Groups = counted
                .GroupBy(c => (Folder: FolderOf(c.Path, installation), c.Kind, Location: SentencePlaceOf(c.Path.LocationId, installation)))
                .Select(g => new BackupOverflowGroup(g.Key.Folder, g.Count(), Size.From((ulong)g.Sum(c => (long)c.Size)), g.Key.Kind, g.Key.Location))
                .OrderByDescending(g => g.Size.Value).ThenBy(g => g.Folder, StringComparer.Ordinal)
                .ToArray(),
            Examples = counted
                .OrderByDescending(c => c.Size).ThenBy(c => c.Path.ToString(), StringComparer.Ordinal)
                .Take(3)
                .Select(c => new BackupOverflowExample(DisplayPathOf(c.Path, installation), Size.From(c.Size), c.Kind))
                .ToArray(),
            ByKind = counted
                .GroupBy(c => c.Kind)
                .ToDictionary(g => g.Key, g => (g.Count(), Size.From((ulong)g.Sum(c => (long)c.Size)))),
        };
    }

    /// <summary>A file the sync would back up, with what the dialog says it is.</summary>
    internal readonly record struct MeasuredFile(GamePath Path, ulong Size, BackupOverflowKind Kind, FileIdentity Identity);

    /// <summary>
    /// A file that exists only on disk looks the same whether a person dropped it there or a game update wrote it.
    /// The store's file list for the installed build tells them apart: a disk-only file with a listed path and size
    /// (and the same hash, when the list's hash is of the kind the sync computes) is one of the game's own files.
    /// </summary>
    internal MeasuredFile[] TellGameFilesApart(IEnumerable<MeasuredFile> measured, GameInstallation installation)
    {
        var all = measured.ToArray();
        if (!all.Any(c => c.Kind == BackupOverflowKind.Appeared && c.Path.LocationId == LocationId.Game)) return all;

        var listed = ListedGameFiles(installation);

        return all.Select(c => c.Kind == BackupOverflowKind.Appeared && IsListed(listed, c.Path, c.Size, c.Identity) ? c with { Kind = BackupOverflowKind.GameFileChanged } : c).ToArray();
    }

    // A list naming the file by SHA-1 and a disk naming it by xxHash3 are compared through the relation recorded for
    // them. With no relation nothing can tell the bytes apart, so path and size decide.
    private bool IsListed(Dictionary<string, GameFileRecord> listed, GamePath path, ulong size, FileIdentity identity)
    {
        if (path.LocationId != LocationId.Game || !listed.TryGetValue(path.Path.ToString(), out var record)) return false;
        if (record.Size.Value != size) return false;

        switch (_identityResolver.Resolve(record.Identity, identity, Size.From(size)))
        {
            case IdentityMatch.Different:
                _perFileLogger.LogTrace("`{Path}` is not the listed file: the list has {ListedIdentity}, the disk has {Identity}", path, record.Identity, identity);
                return false;
            case IdentityMatch.Unknown:
                _perFileLogger.LogTrace("`{Path}` is listed by path and size only: no relation joins the list's {ListedIdentity} to the disk's {Identity}", path, record.Identity, identity);
                return true;
            default:
                return true;
        }
    }

    /// <summary>
    /// What a file the sync would copy is to the player. Read from the node's state and not its source item type,
    /// which defaults to "loadout" on a node that exists only on disk.
    /// </summary>
    private static BackupOverflowKind KindOf(SyncNode node)
    {
        if (node.Actions != (Actions.BackupFile | Actions.IngestFromDisk)) return BackupOverflowKind.MustBackUp;
        if (!node.HavePrevious && !node.HaveLoadout) return BackupOverflowKind.Appeared;
        return node.Signature.HasFlag(Signature.IsGameFile) || (node.HaveLoadout && node.SourceItemType == LoadoutSourceItemType.Game)
            ? BackupOverflowKind.GameFileChanged
            : BackupOverflowKind.ModFileChanged;
    }

    /// <summary>The folder a player would look in: the first two segments, with the location named unless it is the game folder.</summary>
    private static string FolderOf(GamePath path, GameInstallation installation)
    {
        var segments = path.Path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folders = segments.Take(Math.Min(2, segments.Length - 1)).ToArray();
        var isGameFolder = path.LocationId == LocationId.Game;
        if (folders.Length == 0) return isGameFolder ? BackupOverflow.GameFolderName : LocationNameOf(path.LocationId, installation);
        var joined = string.Join('/', folders);
        return isGameFolder ? joined : $"{LocationNameOf(path.LocationId, installation)}/{joined}";
    }

    private static string DisplayPathOf(GamePath path, GameInstallation installation)
        => path.LocationId == LocationId.Game ? path.Path.ToString() : $"{LocationNameOf(path.LocationId, installation)}/{path.Path}";

    /// <summary>The name a player knows the location by: the game's own name for it, else the app's, else "Other folders".</summary>
    private static string LocationNameOf(LocationId id, GameInstallation installation)
        => installation.Game.LocationNames.TryGetValue(id.ToString(), out var name) ? name : id.DisplayName;

    /// <summary>
    /// The location as the backup question's sentence names it: null for the game folder; for a named location the
    /// game's sentence phrase, else the game's label, else the app's sentence phrase; "other folders" for one nobody named.
    /// </summary>
    private static string? SentencePlaceOf(LocationId id, GameInstallation installation)
    {
        if (id == LocationId.Game) return null;
        var key = id.ToString();
        if (installation.Game.LocationSentenceNames.TryGetValue(key, out var gamePhrase)) return gamePhrase;
        if (installation.Game.LocationNames.TryGetValue(key, out var gameLabel)) return gameLabel;
        return id.SentenceName;
    }

    /// <summary>
    /// Applies an adopt answer: files that stay on disk and only want a copy are ingested without one. Every other
    /// node keeps its backup, an intrinsic file included, since the app is about to overwrite what the player had.
    /// </summary>
    private static void ApplyBackupOverflowChoice(Dictionary<GamePath, SyncNode> syncTree, BackupOverflowChoice choice)
    {
        if (choice != BackupOverflowChoice.AdoptInPlace) return;

        foreach (var path in syncTree.Keys.ToArray())
        {
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(syncTree, path);
            if (node.Actions == (Actions.BackupFile | Actions.IngestFromDisk)) node.Actions = Actions.IngestFromDisk;
        }
    }
}

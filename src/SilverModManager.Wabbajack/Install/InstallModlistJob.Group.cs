using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using SilverModManager.Sdk.Diagnostics;
using SilverModManager.Sdk.Hashes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Games.Modlists;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Mapping;
using SilverModManager.Wabbajack.Models;
using BethesdaArchiveException = SilverModManager.Games.Bethesda.Archives.BethesdaArchiveException;
using FileState = SilverModManager.Games.Bethesda.Archives.FileState;
using Ba2Dx10FileState = SilverModManager.Games.Bethesda.Archives.Ba2Dx10FileState;
using PatchException = SilverModManager.Wabbajack.Transforms.PatchException;
using UnreadableTextureException = SilverModManager.Wabbajack.Transforms.UnreadableTextureException;
using UnsupportedTextureFormatException = SilverModManager.Wabbajack.Transforms.UnsupportedTextureFormatException;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Wabbajack.Install;

public sealed partial class InstallModlistJob
{
    /// <summary>
    /// The group this run continues: the one named by <see cref="InstallOptions.UpdateTo"/>, else the one already made
    /// from this list in this loadout. Null for a first install.
    /// </summary>
    /// <exception cref="ModlistUpdateRefusedException">The group cannot be moved to this list.</exception>
    private WabbajackModlistLoadoutGroup.ReadOnly? FindGroup()
    {
        var db = Connection.Db;
        if (Options.UpdateTo is { } updateTo)
        {
            var target = WabbajackModlistLoadoutGroup.Load(db, updateTo.Value);
            if (!target.IsValid() || target.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId != TargetLoadout)
                throw new ModlistUpdateRefusedException($"{Modlist.Name} is not installed in this loadout, so there is nothing to update.");

            var name = target.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name;
            if (IsMidUpdate(target))
            {
                // The update this run would continue; any other waits until that one is resumed or undone.
                if (target.ModlistId.Value != Modlist.Id)
                    throw new ModlistUpdateRefusedException($"The update of {name} is not finished. Finish the update with Resume, or undo it, before starting another.");
            }
            else if (target.ModlistId.Value == Modlist.Id)
            {
                throw new ModlistUpdateRefusedException($"{name} is already at this version of the list.");
            }
            return target;
        }

        foreach (var candidate in WabbajackModlistLoadoutGroup.FindByModlist(db, Modlist))
        {
            if (candidate.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId != TargetLoadout) continue;
            return candidate;
        }
        return null;
    }

    /// <summary>Whether an update switched the group to its list and has not finished installing it.</summary>
    private static bool IsMidUpdate(WabbajackModlistLoadoutGroup.ReadOnly group) => ModlistInstallRuns.IsMidUpdate(group);

    /// <summary>
    /// The group of this list in this loadout, created disabled on the first run. Rows of an earlier run's skips are
    /// cleared. When this run updates the group, the restore point and the switch to this list are written first, so
    /// the group reads as mid-update from the first moment and Apply is refused until the update ends.
    /// </summary>
    private async Task<(LoadoutItemGroupId Group, AbsolutePath Extras)> EnsureGroup(Checked check, WabbajackModlistLoadoutGroup.ReadOnly? existing, CancellationToken cancellationToken)
    {
        // A group an earlier update left unfinished has already been switched; only a fresh update does that.
        var startsUpdate = Options.UpdateTo is not null && existing is { } fresh && !IsMidUpdate(fresh);
        TxId? restorePoint = startsUpdate ? await LoadoutSnapshot.WriteRestorePoint(Connection, TargetLoadout) : null;

        using var tx = Connection.BeginTransaction();
        if (existing is { } found)
        {
            _updateMode = Options.UpdateTo is not null || IsMidUpdate(found);
            // A fresh update's switch is in this transaction, so the list the group is on now is the one it comes from.
            if (restorePoint is not null) _previousModlist = found.ModlistId.Value;
            else if (WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(found, out var previous)) _previousModlist = previous;

            var skips = WabbajackSkippedFile.FindByGroup(Connection.Db, found.Id).ToArray();
            if (restorePoint is { } restore) SwitchToThisList(tx, found, restore, skips);

            // A resumed update has already deleted the old version's notes. They are still in the db as of the restore
            // point, and a game file the old version could not place is not one the user chose to keep.
            if (restorePoint is null && _updateMode && _previousModlist is not null && WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(found, out var earlierPoint))
            {
                foreach (var oldNote in WabbajackSkippedFile.FindByGroup(Connection.AsOf(TxId.From(earlierPoint.Value)), found.Id))
                    _earlierSkips.Add(oldNote.To);
            }

            foreach (var skipped in skips)
            {
                // The note is deleted with the other rows, so the directive it is about has to run again to write it.
                if (skipped.Kind == SkipKind.KeptOriginal) _keptOriginals.Add(skipped.To);
                _earlierSkips.Add(skipped.To);
                tx.Delete(skipped.Id, recursive: false);
            }
            _groupWasInstalled = found.InstalledAt is { HasValue: true };
            // A resume is an unfinished install again until it ends; with InstalledAt left in place it would read as
            // installed from the moment its skip rows are gone.
            if (found.InstalledAt is { HasValue: true } finishedAt)
                tx.Retract(found.Id, WabbajackModlistLoadoutGroup.InstalledAt, finishedAt.Value);
            // A group made before folders carried the group id keeps the folder its tools were registered from.
            var folder = found.ExtrasFolder is { HasValue: true } recorded && recorded.Value.Length > 0
                ? FileSystem.Shared.FromUnsanitizedFullPath(recorded.Value)
                : Paths.ExtrasFor(check.List.Name, found.Id);
            folder.CreateDirectory();
            tx.Add(found.Id, WabbajackModlistLoadoutGroup.ExtrasFolder, folder.ToString());
            await tx.Commit();
            return (found.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId, folder);
        }

        var group = new WabbajackModlistLoadoutGroup.New(tx, out var id)
        {
            ModlistId = Modlist,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id)
                    {
                        Name = check.List.Name,
                        LoadoutId = TargetLoadout,
                        IsDisabled = true,
                    },
                },
            },
        };
        var result = await tx.Commit();
        var created = result.Remap(group);

        // The folder name needs the group's id, so it is recorded once the group exists. It goes in before anything is
        // written, so that removing the group finds the folder even if the install never got as far as writing a tool.
        var extras = Paths.ExtrasFor(check.List.Name, created.Id);
        using var record = Connection.BeginTransaction();
        record.Add(created.Id, WabbajackModlistLoadoutGroup.ExtrasFolder, extras.ToString());
        await record.Commit();
        return (created.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId, extras);
    }

    /// <summary>
    /// Moves <paramref name="group"/> to this list in the transaction that also clears its skip rows: the list it came
    /// from and the restore point written just before are recorded on it, the restore point is protected from garbage
    /// collection until the update is finished, and the group is switched off until the new version has been installed.
    /// </summary>
    private void SwitchToThisList(ITransaction tx, WabbajackModlistLoadoutGroup.ReadOnly group, TxId restorePoint, IReadOnlyList<WabbajackSkippedFile.ReadOnly> skips)
    {
        var db = Connection.Db;
        var item = LoadoutItem.Load(db, group.Id);
        var restore = EntityId.From(restorePoint.Value);

        // A list the install itself left off, for archives it did not have, is not one the user switched off.
        tx.Add(group.Id, WabbajackModlistLoadoutGroup.WasDisabledByUser, item.IsDisabled && skips.All(r => r.Kind.IsByDesign()));
        tx.Add(group.Id, WabbajackModlistLoadoutGroup.PreviousModlist, group.ModlistId.Value);
        tx.Add(group.Id, WabbajackModlistLoadoutGroup.Modlist, Modlist.Id);
        tx.Add(group.Id, WabbajackModlistLoadoutGroup.LastUpdateRestorePoint, restore);
        if (!item.IsDisabled) tx.Add(group.Id, LoadoutItem.Disabled, Null.Instance);

        _ = new ProtectedLoadoutRevision.New(tx)
        {
            LoadoutId = TargetLoadout,
            RevisionId = TransactionId.From(restore),
            Reason = $"Wabbajack update of {item.Name}",
        };
    }

    /// <summary>
    /// Renames the mod groups the new list renamed, in place: the group keeps its id, so its place in the order, its
    /// switch and its rules stay with it, and its files are found under the new name by the finished-directive check.
    /// The display name follows the folder unless the user gave the group a name of their own.
    /// </summary>
    private async Task RenameMovedFolders(Checked check, WriteState state, CancellationToken cancellationToken)
    {
        var gone = InstallReconciler.GoneFolders(state, check.Placements);
        var renamed = InstallReconciler.RenamedFolders(state, check.Placements, gone);
        await RememberPreviousFolders(state, renamed, cancellationToken);
        if (renamed.Count == 0) return;

        var db = Connection.Db;
        using var tx = Connection.BeginTransaction();
        foreach (var (from, to) in renamed)
        {
            var id = state.Groups[from].Value;
            if (LoadoutItem.Load(db, id).Name.Equals(from, StringComparison.OrdinalIgnoreCase)) tx.Add(id, LoadoutItem.Name, to);
            tx.Add(id, WabbajackModGroup.Folder, to);
        }
        await tx.Commit();

        foreach (var (from, to) in renamed)
        {
            Logger.LogInformation("The mod folder '{From}' is '{To}' in the new version of '{List}'; its group is kept", from, to, check.List.Name);
            state.RenameFolder(from, to);
        }
    }

    /// <summary>
    /// Records the folder names the version being replaced used and what its renamed folders are now called, for Order to
    /// read the old profile and tell the groups that existed before the update from the ones it created. When an earlier
    /// run already renamed the groups, the pairs are found the other way round: the groups carry the new names and the
    /// previous version's folders are the ones no group has.
    /// </summary>
    private async Task RememberPreviousFolders(WriteState state, IReadOnlyList<(string From, string To)> renamed, CancellationToken cancellationToken)
    {
        if (!_updateMode || _previousModlist is not { } earlierId) return;

        var previous = await ClassifyVersion(WabbajackModlistLibraryFile.Load(Connection.Db, earlierId), TargetLoadout, Connection, FileStore, cancellationToken);
        if (previous is null) return;

        _previousFolders = InstallReconciler.NamedFolders(previous.Placements);
        if (renamed.Count > 0)
        {
            _renamedFolders = renamed;
            return;
        }

        var notInPrevious = InstallReconciler.GoneFolders(state, previous.Placements);
        _renamedFolders = InstallReconciler.RenamedFolders(state, previous.Placements, notInPrevious).Select(pair => (pair.To, pair.From)).ToArray();
    }

    /// <summary>
    /// Deletes the groups of the folders the new list names nowhere, with their order entries and rules. Runs after the
    /// files are written, so a run that fails part way leaves them for the resume, and a folder emptied only because this
    /// run skipped its files is still named and so stays.
    /// </summary>
    private async Task RemoveGoneGroups(Checked check, WriteState state)
    {
        var gone = InstallReconciler.GoneFolders(state, check.Placements);
        if (gone.Count == 0) return;

        Logger.LogInformation("Removing {Count} mod group(s) the new version of '{List}' no longer has: {Folders}", gone.Count, check.List.Name, string.Join(", ", gone.Order()));
        await LoadoutManager.RemoveItems(gone.Select(folder => state.Groups[folder]).ToArray());
    }

    /// <summary>The game-folder files the user kept: for an update, those the version being replaced placed and the group holds no row or note for.</summary>
    private async Task<IReadOnlySet<RelativePath>> GameFilesTheUserKept(Checked check, WriteState state, CancellationToken cancellationToken)
    {
        if (!_updateMode) return GameFilesKept(state, check.Placements, _earlierSkips, check.Placements, check.List);

        if (_previousModlist is not { } earlierId) return new HashSet<RelativePath>();

        var previous = await ClassifyVersion(WabbajackModlistLibraryFile.Load(Connection.Db, earlierId), TargetLoadout, Connection, FileStore, cancellationToken);
        return previous is null ? new HashSet<RelativePath>() : GameFilesKept(state, check.Placements, _earlierSkips, previous.Placements, previous.List);
    }

    /// <summary>
    /// A version of a list laid out for the loadout, which is the only place to tell which game files it placed and which
    /// were plain copies of the game's own. Null when the library no longer holds the version.
    /// </summary>
    internal static async Task<Classified?> ClassifyVersion(
        WabbajackModlistLibraryFile.ReadOnly version, LoadoutId loadout, IConnection connection, IFileStore fileStore, CancellationToken cancellationToken)
    {
        if (!version.IsValid() || !version.AsLibraryFile().TryGetAsLibraryArchive(out var archive)) return null;
        if (!await ModlistArchive.CanReadModlist(fileStore, archive)) return null;

        return await ClassifyList(loadout, version, connection, fileStore, cancellationToken);
    }
}

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
    internal sealed class WriteState
    {
        private int _archivesStarted;

        /// <summary>How many archives this run builds, for "archive 3 of 12".</summary>
        public int ArchiveCount { get; set; }

        /// <summary>The number the next archive build gets, in the order builds start (they run side by side).</summary>
        public int NextArchiveNumber() => Interlocked.Increment(ref _archivesStarted);

        /// <summary>Directive destinations, folded, to the file store hash and xxHash64 of what was written, for merges to read back and check.</summary>
        public ConcurrentDictionary<string, (FileIdentity Store, FileIdentity XxHash64)> Written { get; } = new();

        /// <summary>Where each kind of skip happened, in the order the rows were written; only the thread that writes rows touches it.</summary>
        public Dictionary<SkipKind, List<string>> Skipped { get; } = new();

        /// <summary>Mod group name to its id: real once committed, a temp id while its transaction is open.</summary>
        public Dictionary<string, LoadoutItemGroupId> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> NewGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Existing mod groups that carry no <see cref="WabbajackModGroup.Folder"/> yet, keyed by their name, which is then also their folder.</summary>
        public HashSet<string> FolderlessGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per group, the files an earlier run (or this one) already created, as path and hash.</summary>
        public Dictionary<string, GroupFiles> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Re-keys a folder's group and files under the name the new list gives it.</summary>
        public void RenameFolder(string from, string to)
        {
            Groups[to] = Groups[from];
            Groups.Remove(from);
            Files[to] = Files[from];
            Files.Remove(from);
        }
    }

    /// <summary>Records the folder of each existing mod group made before groups carried one, from the name it has now, so a later rename keeps finding it.</summary>
    private async Task RecordFolders(WriteState state)
    {
        if (state.FolderlessGroups.Count == 0) return;

        using var tx = Connection.BeginTransaction();
        AddFolders(tx, state);
        await tx.Commit();
        state.FolderlessGroups.Clear();
    }

    /// <summary>Adds the folder, taken from the group's name, to each group in <see cref="WriteState.FolderlessGroups"/>.</summary>
    internal static void AddFolders(ITransaction tx, WriteState state)
    {
        foreach (var name in state.FolderlessGroups) tx.Add(state.Groups[name].Value, WabbajackModGroup.Folder, name);
    }

    /// <summary>The rows the group holds now, by mod folder, as the reconciliation reads them.</summary>
    internal static WriteState LoadExisting(IDb db, LoadoutItemGroupId groupId)
    {
        var state = new WriteState();
        foreach (var child in LoadoutItem.FindByParent(db, groupId).OfTypeLoadoutItemGroup())
        {
            // The settings group is found by its marker and a mod group by the folder it was made from; a user may have renamed either.
            var name = child.IsGameSettingsGroup ? Mo2Layout.ProfileSettingsGroup
                : WabbajackModGroup.Folder.TryGetValue(child, out var folder) ? folder
                : child.AsLoadoutItem().Name;
            state.Groups[name] = child.LoadoutItemGroupId;
            if (!child.IsGameSettingsGroup && !WabbajackModGroup.Folder.TryGetValue(child, out _)) state.FolderlessGroups.Add(name);
            var files = state.Files[name] = new();
            foreach (var file in LoadoutItem.FindByParent(db, child.Id).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
            {
                var path = file.AsLoadoutItemWithTargetPath().TargetPath;
                if (!files.TryGet(path, file.Identity, out var known)) known = files.Add(path, file.Identity, new KnownFile { Size = file.Size });
                known.Ids.Add(file.Id);
                if (WabbajackInstalledFile.ListHash.TryGetValue(file, out var listHash)) known.ListHash = listHash;
            }
        }
        return state;
    }
}

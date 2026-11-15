using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Games.Modlists;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.Diagnostics;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Mapping;
using SilverModManager.Wabbajack.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using WriteState = SilverModManager.Wabbajack.Install.InstallModlistJob.WriteState;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// Moves the loadout tweaks an earlier install put in the list's settings group to where they belong, so the cleanup that
/// deletes rows the list did not ask for does not take the user's edits with it.
/// </summary>
internal sealed class LegacyIniTweaks(IConnection connection, IFileStore fileStore, ILogger logger)
{
    /// <summary>
    /// An earlier version installed the list's initweaks.ini as the loadout's own tweak, and the INI editor wrote the
    /// user's tweaks into the list's settings group, so rows at the loadout's tweak paths in that group may hold
    /// the user's edits. They move to the loadout's own settings group instead of being deleted as rows the list did not
    /// ask for. A row whose bytes are still what the list wrote (this run's text, or the xxHash64 the row recorded when
    /// it was written) is redundant next to the list's tweak and is left to be deleted. When the user's group already
    /// has a tweak for the same INI, the row is merged under it, so the user's own keys win, and then deleted.
    /// Such a row holds the list's whole initweaks text next to the user's keys, so before it moves or merges the keys
    /// whose value is the one the list's tweak of this version sets are dropped: they are the list's, still current, and
    /// kept as the user's they would beat every later version of the list. A key the list's tweak changed or dropped stays
    /// as the user's, since it cannot be told from a deliberate edit. A row left with no keys is deleted.
    /// </summary>
    public async Task KeepUserTweaks(WriteState state, LoadoutId targetLoadout, string listName, Func<GamePath, FileIdentity, bool> isListOutput)
    {
        var legacy = InstallReconciler.UsersTweakRows(state, isListOutput);
        if (legacy.Count == 0) return;

        var db = connection.Db;
        var loadout = Loadout.Load(db, targetLoadout);
        var userRows = UserTweakRows(db, loadout);
        using var tx = connection.BeginTransaction();
        var pins = new List<IDisposable>();
        try
        {
            LoadoutItemGroupId? userGroup = null;
            foreach (var (path, identity, known) in legacy)
            {
                IniTweakLayers.IsLoadoutTweakPath(path, out var target);
                var (remaining, kept) = await WithoutTheListsKeys(state, listName, target, identity);
                if (kept.Count == 0)
                {
                    logger.LogInformation("Dropped the loadout tweak of '{Target}' from an earlier install: every key in it is one the list sets", target);
                }
                else if (userRows.TryGetValue(target, out var user))
                {
                    var under = remaining;
                    under.Merge(IniDocument.Parse(await fileStore.Load(user.Identity)));
                    var bytes = under.ToBytes();
                    pins.Add(await IniEdits.Store(fileStore, path, bytes));
                    var merged = FileIdentity.XxHash3(bytes.xxHash3());
                    tx.Add(user.Id, LoadoutFile.Hash, bytes.xxHash3());
                    tx.Add(user.Id, LoadoutFile.Size, Size.FromLong(bytes.Length));
                    userRows[target] = (user.Id, merged);
                    logger.LogInformation("Merged the loadout tweak of '{Target}' from an earlier install under the user's own, keeping {Keys}", target, kept);
                }
                else
                {
                    var bytes = remaining.ToBytes();
                    pins.Add(await IniEdits.Store(fileStore, path, bytes));
                    var moved = FileIdentity.XxHash3(bytes.xxHash3());
                    userGroup ??= IniEdits.UserSettingsGroup(db, tx, loadout);
                    tx.Add(known.Ids[0], LoadoutItem.ParentId, userGroup.Value);
                    tx.Add(known.Ids[0], LoadoutFile.Hash, bytes.xxHash3());
                    tx.Add(known.Ids[0], LoadoutFile.Size, Size.FromLong(bytes.Length));
                    userRows[target] = (known.Ids[0], moved);
                    known.Ids.RemoveAt(0);
                    logger.LogInformation("Kept the loadout tweak of '{Target}' as the user's, with {Keys}: an earlier install had put it in the list's settings group", target, kept);
                }

                // What is left of the legacy row is folded into the user's tweak or a duplicate of it.
                foreach (var id in known.Ids) tx.Delete(id, recursive: false);
                known.Ids.Clear();
            }
            await tx.Commit();
        }
        finally
        {
            foreach (var pin in pins) pin.Dispose();
        }
    }

    /// <summary>
    /// A legacy tweak without the keys the list's tweak of the same INI sets to the same value, and the keys left, as
    /// <c>[section] key</c> names for the log.
    /// </summary>
    private async Task<(IniDocument Remaining, IReadOnlyList<string> Kept)> WithoutTheListsKeys(WriteState state, string listName, string target, FileIdentity legacy)
    {
        var document = IniDocument.Parse(await fileStore.Load(legacy));
        var listPath = IniTweakLayers.TweakPath(IniTweakLayers.LayerName(listName), target);
        if (state.Files.TryGetValue(Mo2Layout.ProfileSettingsGroup, out var files))
        {
            foreach (var (identity, known) in files.AtPath(listPath))
            {
                if (!known.Wanted) continue;
                var list = IniDocument.Parse(await fileStore.Load(identity));
                document = WithoutLinesSetByTheList(document, list);
            }
        }

        var kept = document.Sections.SelectMany(section => document.Entries(section).Select(entry => $"[{section}] {entry.Key}")).ToArray();
        return (document, kept);
    }

    // Every line of `document` that sets a key to the value `list` sets it to, not just the first copy of the key.
    internal static IniDocument WithoutLinesSetByTheList(IniDocument document, IniDocument list)
    {
        var listed = list.Sections.SelectMany(section => list.Entries(section).Select(e => (Section: section, e.Key, e.Value))).ToArray();
        var text = new StringBuilder();
        var section = "";
        foreach (var line in document.Lines)
        {
            if (line.Kind == IniLineKind.Section) section = line.Name;
            var listsIt = line.Kind == IniLineKind.KeyValue && listed.Any(l =>
                string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase)
                && string.Equals(l.Key, line.Name, StringComparison.OrdinalIgnoreCase)
                && l.Value == line.Value);
            if (!listsIt) text.Append(line.Text).Append(line.Terminator);
        }
        return IniDocument.Parse(text.ToString(), document.Encoding);
    }

    /// <summary>The loadout's own tweak rows in its top-level settings group, by the INI each one changes.</summary>
    private static Dictionary<string, (EntityId Id, FileIdentity Identity)> UserTweakRows(IDb db, Loadout.ReadOnly loadout)
    {
        var rows = new Dictionary<string, (EntityId, FileIdentity)>(StringComparer.OrdinalIgnoreCase);
        if (IniEdits.GameSettingsGroupOf(db, loadout) is not { } group) return rows;

        foreach (var file in LoadoutItem.FindByParent(db, group).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
        {
            var path = (GamePath)file.AsLoadoutItemWithTargetPath().TargetPath;
            if (IniTweakLayers.IsLoadoutTweakPath(path, out var target)) rows.TryAdd(target, (file.Id, file.Identity));
        }
        return rows;
    }
}

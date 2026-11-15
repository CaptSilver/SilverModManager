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
    internal sealed record ProfileTexts(string? ModlistTxt, string? PluginsTxt, string? LoadorderTxt);

    /// <summary>
    /// Puts the mod groups in the order MO2 would give them and switches off the ones its modlist.txt disables, stages the
    /// profile's plugin list on the loadout's own plugin order, and enables the list when every directive was executed.
    /// Every write is in one transaction, so a failure leaves the list disabled and the next run does it again.
    /// </summary>
    private async Task Order(Checked check, LoadoutItemGroupId modlistGroup, WriteState state, CancellationToken cancellationToken)
    {
        Progress.Enter(InstallPhase.Order, 0, 0);
        var installation = check.Loadout.InstallationInstance;
        var target = RequireTarget(installation);
        var texts = await ReadProfileTexts(FileStore, check.Placements, check.ListArchive, cancellationToken);
        var previous = await PreviousProfile(cancellationToken);

        // The sort order manager creates the loadout's order a moment after the loadout appears, so it may not be there yet.
        var sortOrderId = await RequirePluginVariety(installation).GetOrCreateSortOrderFor(TargetLoadout, TargetLoadout, cancellationToken);

        var db = Connection.Db;
        var children = LoadoutItem.FindByParent(db, modlistGroup).OfTypeLoadoutItemGroup().ToArray();
        var entries = texts.ModlistTxt is null ? [] : ModlistTxt.Parse(texts.ModlistTxt);
        var rootNames = StockGameFolders(check);
        var order = Mo2ProfileOrderReader.Read(children, entries, rootNames, check.List.Name, Logger);

        // An update with the previous profile at hand merges against it; without it the list's order and switches apply in full.
        UpdateOrderPlan? plan = null;
        if (_updateMode && previous is not null)
        {
            // The groups the previous version had: those named by its directives, under the name a rename gave them.
            var renamedTo = _renamedFolders.Select(r => r.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existed = state.Groups.Where(pair => _previousFolders.Contains(pair.Key) || renamedTo.Contains(pair.Key)).Select(pair => pair.Value).ToHashSet();
            var oldEntries = previous.ModlistTxt is null ? [] : UpdateOrderPlanner.Rename(ModlistTxt.Parse(previous.ModlistTxt), _renamedFolders);
            plan = UpdateOrderPlanner.ForContainer(db, modlistGroup, children, existed, oldEntries, order, rootNames, check.List.Name);
            order = order with { WinnerToLoser = plan.Entries };
        }
        else if (_updateMode)
        {
            Logger.LogWarning("{Warning}", UpdateOrderPlanner.InstalledVersionGoneWarning);
        }

        // The importer's priority write must be the only one in this transaction, so it opens fresh. The profile's own plugin
        // order is staged below, so the importer must not seed the plugin variety from the mod order as well.
        using var tx = Connection.BeginTransaction();
        // Read fresh: the groups this run created are not in the loadout the check loaded, and the sink names them in its drops.
        OrderImporter.Import(Loadout.Load(db, TargetLoadout), order, new ModOrderSource($"Wabbajack list {check.List.Name}", modlistGroup, Manifest: null, SeedVarieties: false, NewGroups: plan?.NewGroups), tx);

        var conflicts = new List<string>(plan?.Conflicts ?? []);
        foreach (var (group, enabled) in plan?.FlagWrites ?? [])
        {
            var item = LoadoutItem.Load(db, group);
            if (enabled) tx.Retract(item.Id, LoadoutItem.Disabled, Null.Instance);
            else tx.Add(item.Id, LoadoutItem.Disabled, Null.Instance);
        }

        if (texts.PluginsTxt is not null || texts.LoadorderTxt is not null)
        {
            var loadout = Loadout.Load(db, TargetLoadout);
            if (_updateMode && previous is not null)
                target.StagePluginList(loadout, sortOrderId, new ProfilePluginTexts(previous.PluginsTxt, previous.LoadorderTxt), new ProfilePluginTexts(texts.PluginsTxt, texts.LoadorderTxt), tx, conflicts);
            else
                target.StagePluginList(loadout, sortOrderId, texts.PluginsTxt, texts.LoadorderTxt, tx);
        }
        else
            Logger.LogWarning("The profile of '{List}' has neither a plugins.txt nor a loadorder.txt, so the plugin order is left as it was", check.List.Name);

        foreach (var conflict in conflicts)
            Logger.LogInformation("The update of '{List}' left {Conflict} as the user had it; the new version of the list puts it elsewhere", check.List.Name, conflict);

        tx.Add(modlistGroup.Value, WabbajackModlistLoadoutGroup.InstalledAt, DateTimeOffset.UtcNow);
        // A list the user switched off before the update stays off; the note has done its work either way.
        var finished = WabbajackModlistLoadoutGroup.Load(db, modlistGroup.Value);
        var userSwitchedItOff = WabbajackModlistLoadoutGroup.WasDisabledByUser.TryGetValue(finished, out var switchedOff) && switchedOff;
        if (WabbajackModlistLoadoutGroup.WasDisabledByUser.TryGetValue(finished, out var note))
            tx.Retract(modlistGroup.Value, WabbajackModlistLoadoutGroup.WasDisabledByUser, note);
        // A kept original is on disk and the game loads it, and a tool folder never held the file, so only rows for content that is missing keep the list off.
        if (!userSwitchedItOff && !WabbajackSkippedFile.SkipsThatKeepTheListOff(db, modlistGroup).Any() && LoadoutItem.Load(db, modlistGroup).IsDisabled)
            tx.Retract(modlistGroup.Value, LoadoutItem.Disabled, Null.Instance);

        await tx.Commit();
    }

    /// <summary>The profile files of the version this update replaces; null when it is not an update or the library no longer holds that version.</summary>
    private async Task<ProfileTexts?> PreviousProfile(CancellationToken cancellationToken)
    {
        if (!_updateMode || _previousModlist is not { } earlierId) return null;

        var previous = await ClassifyVersion(WabbajackModlistLibraryFile.Load(Connection.Db, earlierId), TargetLoadout, Connection, FileStore, cancellationToken);
        return previous is null ? null : await ReadProfileTexts(FileStore, previous.Placements, previous.ListArchive, cancellationToken);
    }

    /// <summary>The profile files of the list in <paramref name="archive"/>, whose directives are <paramref name="placements"/>.</summary>
    internal static async Task<ProfileTexts> ReadProfileTexts(IFileStore fileStore, IReadOnlyList<Placement> placements, LibraryArchive.ReadOnly archive, CancellationToken cancellationToken)
    {
        string? modlist = null, plugins = null, loadorder = null;
        foreach (var placement in placements)
        {
            if (placement.Target is not Mo2Target.Seed seed || placement.Directive is not InlineFile inline) continue;
            if (seed.Kind is not (SeedKind.ModlistTxt or SeedKind.PluginsTxt or SeedKind.LoadorderTxt)) continue;

            await using var stream = await ModlistArchive.ReadBlob(fileStore, archive, inline.SourceDataId, cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(cancellationToken);
            switch (seed.Kind)
            {
                case SeedKind.ModlistTxt: modlist = text; break;
                case SeedKind.PluginsTxt: plugins = text; break;
                default: loadorder = text; break;
            }
        }
        return new ProfileTexts(modlist, plugins, loadorder);
    }
}

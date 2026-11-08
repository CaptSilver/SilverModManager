using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel.Undo;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Update;

namespace SilverModManager.Wabbajack.Cli;

public static class Verbs
{
    /// <summary>The <c>wabbajack</c> verbs; they need nothing else from the module.</summary>
    public static IServiceCollection AddWabbajackVerbs(this IServiceCollection collection) =>
        collection
            .AddModule("wabbajack", "Commands for Wabbajack modlists")
            .AddVerb(() => Inspect)
            .AddVerb(() => List)
            .AddVerb(() => Download)
            .AddVerb(() => Status)
            .AddVerb(() => Cancel)
            .AddVerb(() => Updates)
            .AddVerb(() => Update)
            .AddVerb(() => FinishUpdate)
            .AddVerb(() => UndoUpdate);

    private sealed record KindCount(string Kind, int Count);

    private sealed record InspectedArchive(string Name, long Size, string Source, string Detail);

    private sealed record InspectedModlist(string Name, string Author, string Version, string GameType, int ArchiveCount, int DirectiveCount,
        KindCount[] Directives, KindCount[] Sources, InspectedArchive[] Archives);

    // Reads the zip directly: looking at a list must not add it to the library.
    [Verb("wabbajack inspect", "Prints what a .wabbajack file contains: its details, directive and source counts, and its archives")]
    private static async Task<int> Inspect([Injected] IRenderer renderer,
        [Option("f", "file", "The .wabbajack file")] AbsolutePath file,
        [Option("j", "json", "Print the details as JSON", true)] bool json)
    {
        if (!file.FileExists)
        {
            await renderer.TextLine("{0} does not exist", file.ToString());
            return 1;
        }

        ModList list;
        try
        {
            using var zip = ZipFile.OpenRead(file.ToString());
            var entry = zip.Entries.FirstOrDefault(e => ModlistEntryNames.Contains(e.FullName, StringComparer.OrdinalIgnoreCase));
            if (entry is null)
            {
                await renderer.TextLine("{0} has no 'modlist' entry, so it is not a Wabbajack modlist", file.ToString());
                return 1;
            }
            await using var stream = entry.Open();
            list = ModListReader.Read(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnsupportedModlistException)
        {
            await renderer.TextLine("{0} cannot be read as a modlist: {1}", file.ToString(), ex.Message);
            return 1;
        }

        if (json)
        {
            var inspected = new InspectedModlist(list.Name, list.Author, list.Version, list.GameType.ToString(), list.Archives.Count, list.Directives.Count,
                ModlistInspection.DirectiveKinds(list).Select(k => new KindCount(k.Kind, k.Count)).ToArray(),
                ModlistInspection.SourceKinds(list).Select(k => new KindCount(k.Kind, k.Count)).ToArray(),
                list.Archives.Select(a => new InspectedArchive(a.Name, a.Size, ModlistInspection.SourceName(a.State), ModlistInspection.SourceDetail(a.State))).ToArray());
            await renderer.TextLine(JsonSerializer.Serialize(inspected, CliJson.Options));
            return 0;
        }

        await renderer.TextLine("Name: {0}", list.Name);
        await renderer.TextLine("Author: {0}", list.Author);
        await renderer.TextLine("Version: {0}", list.Version);
        await renderer.TextLine("GameType: {0}", list.GameType);
        await renderer.TextLine("Archives: {0}, directives: {1}", list.Archives.Count, list.Directives.Count);

        await renderer.Table(["Directive", "Count"], ModlistInspection.DirectiveKinds(list).Select(k => new object[] { k.Kind, k.Count }));
        await renderer.Table(["Source", "Count"], ModlistInspection.SourceKinds(list).Select(k => new object[] { k.Kind, k.Count }));
        await renderer.Table(["Archive", "Size", "Source", "Detail"], list.Archives.Select(ModlistInspection.ArchiveRow));
        return 0;
    }

    private sealed record ModlistRow(string Id, string Name, string Version, string Game, int Archives, bool Downloading);

    [Verb("wabbajack list", "Lists the modlists in the library with their ids, and marks the one whose archives are downloading")]
    private static async Task<int> List([Injected] IRenderer renderer, [Injected] IModlistCatalog catalog, [Injected] IArchiveAcquirer acquirer,
        [Option("j", "json", "Print the modlists as JSON", true)] bool json)
    {
        var running = acquirer.Status is { IsRunning: true } status ? status.Modlist : null;
        if (json)
        {
            var rows = catalog.List().Select(m => new ModlistRow(m.HexId, m.Name, m.Version, m.GameType, m.ArchiveCount, m.Id == running));
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        await renderer.Table(["Id", "Name", "Version", "Game", "Archives", "Batch"],
            catalog.List().Select(m => (object[])BatchStatusText.ListRow(m, m.Id == running)));
        return 0;
    }

    [Verb("wabbajack download", "Starts downloading a modlist's archives in the running app and returns at once. Progress shows on the modlist's page and in 'wabbajack status'")]
    private static async Task<int> Download([Injected] IRenderer renderer, [Injected] IModlistCatalog catalog, [Injected] ModlistDownloads downloads,
        [Injected] IServiceProvider services, [Injected] CancellationToken token,
        [Option("m", "modlist", "The modlist: its name, its id from 'wabbajack list', or the file name of its .wabbajack in the library")] string modlist)
    {
        // The batch outlives this command, which only the window process can allow: a headless main exits with the verb.
        if (services.GetService<WindowHost>() is null)
            throw new WindowRequiredException("Downloading a modlist needs the SilverModManager window to be open, because the downloads go on after this command ends. Open SilverModManager and try again.");

        var start = await downloads.Start(catalog.Find(modlist), token);
        await renderer.TextLine("{0}", BatchStatusText.Started(start));
        return 0;
    }

    private sealed record BatchFailure(string Archive, string Reason);

    private sealed record BatchStatus(bool Running, string? Modlist = null, int Finished = 0, int Total = 0, long BytesFetched = 0, long? BytesPerSecond = null,
        int WaitingOnBrowser = 0, int WaitingByHand = 0, BatchFailure[]? Failures = null);

    [Verb("wabbajack status", "Shows whether a modlist download is running: archives done, bytes fetched, what waits on you, and the latest failures")]
    private static async Task<int> Status([Injected] IRenderer renderer, [Injected] IModlistCatalog catalog, [Injected] IArchiveAcquirer acquirer,
        [Option("m", "modlist", "Only report this modlist (name, id or file name); without it, whichever batch ran last", true)] string? modlist,
        [Option("j", "json", "Print the status as JSON; running is false when no batch has run", true)] bool json)
    {
        var wanted = modlist is null ? null : catalog.Find(modlist);
        if (acquirer.Status is not { } status || (wanted is not null && status.Modlist != wanted.Id))
        {
            if (json)
            {
                await renderer.TextLine(JsonSerializer.Serialize(new BatchStatus(Running: false), CliJson.Options));
                return 0;
            }

            await renderer.TextLine("{0}", wanted is null
                ? "No download batch has run since the app started."
                : $"No download batch has run for '{wanted.Name}' since the app started.");
            return 0;
        }

        var name = catalog.List().FirstOrDefault(m => m.Id == status.Modlist)?.Name ?? "a modlist";
        var plan = acquirer.Plan.Modlist == status.Modlist ? acquirer.Plan : new ArchivePlan([]);
        if (json)
        {
            var counts = PlanCounts.Of(plan);
            var batch = new BatchStatus(status.IsRunning, name, status.Finished, status.Total, status.BytesFetched, status.BytesPerSecond,
                counts.Browser, counts.Manual, status.Failures.Select(f => new BatchFailure(f.Archive, f.Reason)).ToArray());
            await renderer.TextLine(JsonSerializer.Serialize(batch, CliJson.Options));
            return 0;
        }

        foreach (var line in BatchStatusText.Describe(status, name, plan)) await renderer.TextLine("{0}", line);
        return 0;
    }

    [Verb("wabbajack cancel", "Stops the modlist download that is running, and does not resume it after a restart")]
    private static async Task<int> Cancel([Injected] IRenderer renderer, [Injected] IArchiveAcquirer acquirer, [Injected] ModlistDownloads downloads)
    {
        if (acquirer.Status is not { IsRunning: true })
        {
            await renderer.TextLine("No modlist download is running.");
            return 0;
        }

        downloads.Cancel();
        await renderer.TextLine("Stopping the running modlist download.");
        return 0;
    }

    private sealed record UpdateRow(string Modlist, string Installed, string Newer, bool Reupload, int Archives, long DownloadBytes, bool OfflineCopy, DateTimeOffset ListingFetchedAt);

    [Verb("wabbajack updates", "Lists the modlists installed in a loadout that the gallery has a newer version of, with the archive count and size of the whole new list")]
    private static async Task<int> Updates([Injected] IRenderer renderer, [Injected] ModlistUpdateCheck check, [Injected] CancellationToken token,
        [Option("l", "loadout", "Loadout whose modlists to check")] Loadout.ReadOnly loadout,
        [Option("j", "json", "Print the updates as JSON", true)] bool json)
    {
        var updates = await check.Check(loadout.LoadoutId, token);
        string NameOf(ModlistUpdate update) => update.Group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name;

        if (json)
        {
            var rows = updates.Select(u => new UpdateRow(NameOf(u), u.Installed.Version, u.Newer.Version, u.IsReupload, u.ArchivesToDownload, (long)u.DownloadSize.Value, u.IsStale, u.ListingFetchedAt));
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        if (updates.Count == 0)
        {
            await renderer.TextLine("Nothing to do: no newer version of an installed modlist is known.");
            return 0;
        }

        if (updates.FirstOrDefault(u => u.IsStale) is { } stale)
            await renderer.TextLine("Offline copy from {0}: the gallery could not be reached, so newer versions may have been released since.",
                stale.ListingFetchedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture));

        await renderer.Table(["Modlist", "Installed", "Newer", "Archives (list total)", "Download size"], updates.Select(u => new object[]
        {
            NameOf(u),
            u.Installed.Version,
            u.IsReupload ? $"{u.Newer.Version} (re-uploaded by the author)" : u.Newer.Version,
            u.ArchivesToDownload.ToString(),
            u.DownloadSize.ToString(),
        }));
        return 0;
    }

    private sealed record RenamedFolder(string From, string To);

    private sealed record UpdateReviewJson(string Modlist, string Installed, string Newer, int ArchivesToDownload, long DownloadBytes,
        string[] Added, string[] Changed, RenamedFolder[] Renamed, string[] Removed, string[] ModOrder, string[] Plugins, string[] Conflicts,
        string[] RetriedTextures, string[] GameFilesKept, string[] UserTweaksKept, string[] Warnings);

    private sealed record UpdateResultJson(string Modlist, string Installed, string Newer, int? RestorePointRevision, ulong? RestorePointTransaction, string UndoVerb);

    [Verb("wabbajack update", "Moves an installed modlist to a newer version of it. Without --yes it prints what the update would do and changes nothing; with --yes it downloads the missing archives, writes a restore point and installs the new version")]
    private static async Task<int> Update([Injected] IRenderer renderer, [Injected] IModlistCatalog catalog, [Injected] IConnection connection,
        [Injected] ModlistUpdater updater, [Injected] IServiceProvider services, [Injected] CancellationToken token,
        [Option("m", "modlist", "The new version of the list: its name, its id from 'wabbajack list', or the file name of its .wabbajack in the library")] string modlist,
        [Option("l", "loadout", "Loadout the list is installed in")] Loadout.ReadOnly loadout,
        [Option("f", "from", "The installed list to update, when the new list has no gallery identity to find it by", true)] string? from,
        [Option("r", "review", "List every folder, file and archive the update touches, not only the counts", true)] bool review,
        [Option("y", "yes", "Apply the update; without it nothing is changed", true)] bool yes,
        [Option("s", "skip-missing", "Apply even when archives are still missing; the files that need them are skipped", true)] bool skipMissing,
        [Option("j", "json", "Print the review, or the result of the update, as JSON", true)] bool json)
    {
        // The downloads and the install run on the window's job monitor, which a headless main does not have.
        if (yes && services.GetService<WindowHost>() is null)
            throw new WindowRequiredException("Updating a modlist needs the SilverModManager window to be open, because the archive downloads and the install run in the app. Open SilverModManager and try again.");

        var newFile = WabbajackModlistLibraryFile.Load(connection.Db, catalog.Find(modlist).Id.Value);
        var group = InstalledGroup(catalog, connection.Db, loadout, newFile, from);
        var name = ModlistUpdater.NameOf(group);
        var installed = group.Modlist.Version;

        // The refusals Apply makes before it writes, asked now so a refused update neither downloads nor promises --yes.
        updater.EnsureCanApply(group, newFile);

        var preview = await updater.Review(group, newFile, token);
        if (json && !yes)
        {
            await renderer.TextLine(JsonSerializer.Serialize(new UpdateReviewJson(name, installed, newFile.Version, preview.Downloads.Count, (long)preview.DownloadSize.Value,
                [.. preview.Added], [.. preview.Changed], [.. preview.Renamed.Select(r => new RenamedFolder(r.From, r.To))], [.. preview.Removed],
                [.. preview.ModOrder], [.. preview.Plugins], [.. preview.Conflicts], [.. preview.RetriedTextures],
                [.. preview.GameFilesKept.Select(path => path.ToString())], [.. preview.UserTweaksKept.Select(path => path.ToString())], [.. preview.Warnings]), CliJson.Options));
            return 0;
        }

        if (!json)
        {
            await renderer.TextLine("{0}", UpdateReviewText.Header(name, installed, newFile.Version, preview));
            foreach (var line in UpdateReviewText.Describe(preview, listItems: review)) await renderer.TextLine("{0}", line);
        }

        if (!yes)
        {
            await renderer.TextLine("{0}", UpdateReviewText.RestorePointNote);
            await renderer.TextLine("Nothing was changed. Run the same command with --yes to apply the update.");
            return 0;
        }

        if (preview.Downloads.Count > 0 && !await updater.IsDownloaded(newFile, loadout.LoadoutId, token))
        {
            if (!json) await renderer.TextLine("Downloading the archives the library lacks. 'wabbajack status' shows how far it has got.");
            await updater.Download(newFile, loadout.LoadoutId, token);
        }

        var applied = await updater.Apply(group, newFile, skipMissing, preview.GameFilesKept.ToHashSet(), token);
        var restorePoint = updater.RestorePointOf(applied);
        var revision = restorePoint is { } point ? RevisionNumber(services.GetRequiredService<UndoService>(), loadout, point) : null;
        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(new UpdateResultJson(name, installed, newFile.Version, revision, restorePoint?.Value, UndoCommand(newFile, loadout)), CliJson.Options));
            return 0;
        }

        await renderer.TextLine("Updated {0} from {1} to {2}.", name, installed, newFile.Version);
        if (revision is { } number) await renderer.TextLine("restore point: revision {0} in 'loadout revisions'", number);
        else if (restorePoint is { } written) await renderer.TextLine("restore point: transaction {0:X}", written.Value);
        await renderer.TextLine("To go back to {0}: {1}", installed, UndoCommand(newFile, loadout));
        await renderer.TextLine("'wabbajack finish-update' releases the restore point and can remove the old version's archives.");
        return 0;
    }

    private static string UndoCommand(WabbajackModlistLibraryFile.ReadOnly list, Loadout.ReadOnly loadout)
        => $"wabbajack undo-update -m \"{list.AsLibraryFile().FileName}\" -l {loadout.LoadoutId}";

    private sealed record UndoResultJson(string Modlist, string RestoredVersion);

    [Verb("wabbajack undo-update", "Puts a modlist back to the version it had before its update, using the update's restore point, and removes the protection the restore point held on the old version's files")]
    private static async Task<int> UndoUpdate([Injected] IRenderer renderer, [Injected] IModlistCatalog catalog, [Injected] IConnection connection,
        [Injected] ModlistUpdater updater, [Injected] UndoService undo, [Injected] CancellationToken token,
        [Option("m", "modlist", "The updated list as it is installed now: its name, its id from 'wabbajack list', or the file name of its .wabbajack in the library")] string modlist,
        [Option("l", "loadout", "Loadout the list is installed in")] Loadout.ReadOnly loadout,
        [Option("j", "json", "Print the result as JSON", true)] bool json)
    {
        var installed = catalog.Find(modlist);
        var group = GroupOn(InstalledGroups(connection.Db, loadout), installed, loadout);
        var name = ModlistUpdater.NameOf(group);
        var previous = WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(group, out var previousId)
            ? WabbajackModlistLibraryFile.Load(connection.Db, previousId).Version
            : "";

        await updater.Undo(group, tx => undo.RevertTo(new LoadoutRevision(loadout.Id, EntityId.From(tx.Value), DateTimeOffset.UtcNow)), token);

        if (json) await renderer.TextLine(JsonSerializer.Serialize(new UndoResultJson(name, previous), CliJson.Options));
        else await renderer.TextLine("Undid the update of {0}{1}.", name, previous.Length > 0 ? $": it is back on {previous}" : "");
        return 0;
    }

    private sealed record FinishResultJson(string Modlist, string OldVersion, bool OldVersionRemoved, string[] RemovedArchives, string[] KeptArchives);

    [Verb("wabbajack finish-update", "Ends an update for good: releases the restore point, so the update can no longer be undone, and with --delete-old-archives removes the old version's list file and the archives only it used")]
    private static async Task<int> FinishUpdate([Injected] IRenderer renderer, [Injected] IModlistCatalog catalog, [Injected] IConnection connection,
        [Injected] ModlistUpdateFinish finisher, [Injected] CancellationToken token,
        [Option("m", "modlist", "The updated list as it is installed now: its name, its id from 'wabbajack list', or the file name of its .wabbajack in the library")] string modlist,
        [Option("l", "loadout", "Loadout the list is installed in")] Loadout.ReadOnly loadout,
        [Option("d", "delete-old-archives", "Also remove the old version's list file and the archives only it used", true)] bool deleteOldArchives,
        [Option("j", "json", "Print what was released and removed as JSON", true)] bool json)
    {
        var db = connection.Db;
        var installed = catalog.Find(modlist);
        var group = GroupOn(InstalledGroups(db, loadout), installed, loadout);
        if (!WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(group, out _) && !WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(group, out _))
            throw new ModlistUpdateRefusedException($"{installed.Name} has no update to finish in {loadout.Name}.");

        // Read before the finish: the archives it removes are gone afterwards.
        var plan = finisher.Plan(group).OldVersion;
        var removable = plan.OwnedArchives.Select(id => LibraryItem.Load(db, id.Value).Name).ToArray();
        var kept = plan.Kept.Select(k => $"{k.Name}: {k.Reason}").ToArray();

        await finisher.Finish(group, deleteOldArchives, token);

        var oldRemoved = deleteOldArchives && plan.Name.Length > 0 && !WabbajackModlistLibraryFile.Load(connection.Db, plan.Modlist.Value).IsValid();
        string[] removedArchives = oldRemoved ? removable : [];
        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(new FinishResultJson(installed.Name, plan.Name, oldRemoved, removedArchives, kept), CliJson.Options));
            return 0;
        }

        await renderer.TextLine("Finished the update of {0}: the restore point is released, so the update can no longer be undone.", installed.Name);
        if (oldRemoved)
        {
            await renderer.TextLine("Removed the old version {0} from the library.", plan.Name);
            foreach (var archive in removedArchives) await renderer.TextLine("Removed archive {0}", archive);
        }
        else if (plan.Name.Length > 0)
        {
            await renderer.TextLine("The old version {0} stays in the library.{1}", plan.Name,
                deleteOldArchives ? "" : removable.Length > 0 ? $" --delete-old-archives removes it with the archives only it used: {string.Join(", ", removable)}." : " --delete-old-archives removes it.");
        }

        foreach (var archive in kept) await renderer.TextLine("Kept {0}", archive);
        return 0;
    }

    private static IEnumerable<WabbajackModlistLoadoutGroup.ReadOnly> InstalledGroups(IDb db, Loadout.ReadOnly loadout)
        => WabbajackModlistLoadoutGroup.All(db).Where(group => group.IsValid() && ModlistUpdater.LoadoutOf(group) == loadout.LoadoutId);

    private static WabbajackModlistLoadoutGroup.ReadOnly GroupOn(IEnumerable<WabbajackModlistLoadoutGroup.ReadOnly> groups, ModlistInfo list, Loadout.ReadOnly loadout)
    {
        foreach (var group in groups)
            if (group.ModlistId.Value == list.Id.Value) return group;
        throw new ModlistUpdateRefusedException($"{list.Name} is not installed in {loadout.Name}.");
    }

    /// <summary>
    /// The install being updated: the one named with <paramref name="from"/>, else the one on the same gallery list as
    /// <paramref name="newFile"/> (or already on <paramref name="newFile"/>, for an update waiting to be resumed).
    /// </summary>
    private static WabbajackModlistLoadoutGroup.ReadOnly InstalledGroup(
        IModlistCatalog catalog, IDb db, Loadout.ReadOnly loadout, WabbajackModlistLibraryFile.ReadOnly newFile, string? from)
    {
        var groups = InstalledGroups(db, loadout).ToArray();
        if (from is not null)
        {
            return GroupOn(groups, catalog.Find(from), loadout);
        }

        var matches = groups.Where(group => group.ModlistId.Value == newFile.Id || ModlistIdentity.SameList(group.Modlist, newFile)).ToArray();
        return matches.Length switch
        {
            1 => RefuseDowngrade(matches[0], newFile),
            0 => throw new ModlistUpdateRefusedException($"No modlist in {loadout.Name} is an earlier version of {newFile.Name} {newFile.Version}. Name the installed list with --from."),
            _ => throw new ModlistUpdateRefusedException(
                $"More than one modlist in {loadout.Name} is on {newFile.Name}'s gallery list: {string.Join(", ", matches.Select(ModlistUpdater.NameOf))}. Name the one to update with --from."),
        };
    }

    /// <summary>Updates only move forward: the superseded previous file, or an older release, is not a version to move to.</summary>
    private static WabbajackModlistLoadoutGroup.ReadOnly RefuseDowngrade(WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly newFile)
    {
        if (group.ModlistId.Value == newFile.Id) return group;

        var isPrevious = WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(group, out var previous) && previous == newFile.Id;
        if (isPrevious || ModlistIdentity.IsOlder(newFile, group.Modlist))
            throw new ModlistUpdateRefusedException(
                $"{newFile.Name} {newFile.Version} is not newer than the installed {group.Modlist.Version}, and an update only moves forward. 'wabbajack undo-update' goes back to the version before the last update.");
        return group;
    }

    /// <summary>
    /// The number <c>loadout revert -r</c> takes for the state at <paramref name="restorePoint"/>: the newest revision
    /// at or before it, since a revision exists only for a transaction that changed the loadout. Null when no revision precedes it.
    /// </summary>
    private static int? RevisionNumber(UndoService undo, Loadout.ReadOnly loadout, TxId restorePoint)
    {
        var revisions = undo.RevisionsFor(loadout.Id).OrderBy(r => r.Revision.Timestamp).ToList();
        var index = revisions.FindLastIndex(r => r.Revision.TxEntity.Value <= restorePoint.Value);
        return index < 0 ? null : index;
    }

    private static readonly string[] ModlistEntryNames = ["modlist", "modlist.json"];
}

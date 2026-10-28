using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Collections;

/// <summary>What <see cref="CollectionUpdater.Repair"/> changed in a collection, or would change on a dry run.</summary>
/// <param name="Repointed">Mods whose entry named a different download than the file they were installed from. They now name their own.</param>
/// <param name="Removed">Old versions of mods that were removed because their one replacement from the current revision is installed.</param>
/// <param name="Dropped">Mods taken out of the collection and moved to the loadout's own: the current revision doesn't list them, or which version replaces them isn't certain.</param>
/// <param name="SettingsChanged">Mods installed from the right file whose install settings (fomod choices, patches, file list) differ in the current revision. Nothing is done about them; reinstalling them from the collection page applies the new settings.</param>
/// <param name="SkippedRules">The user's rules that couldn't move to a replacement because they would contradict other rules.</param>
/// <param name="DryRun">Whether this only describes the repair and nothing was changed.</param>
public sealed record CollectionRepairResult(
    IReadOnlyList<string> Repointed,
    IReadOnlyList<string> Removed,
    IReadOnlyList<DroppedMod> Dropped,
    IReadOnlyList<string> SettingsChanged,
    IReadOnlyList<string> SkippedRules,
    bool DryRun)
{
    /// <summary>A result for a collection that needed nothing.</summary>
    public static CollectionRepairResult Nothing(bool dryRun) => new([], [], [], [], [], dryRun);

    /// <summary>Whether the collection needed repairing. Changed install settings are only reported, so they don't count.</summary>
    public bool ChangedAnything => Repointed.Count > 0 || Removed.Count > 0 || Dropped.Count > 0;

    /// <summary>The lines the <c>collection repair</c> command prints for one collection: a count per kind of change, then the mods.</summary>
    public IEnumerable<string> Describe(string collectionName)
    {
        if (ChangedAnything is false && SettingsChanged.Count == 0)
        {
            yield return $"{collectionName}: nothing to repair";
            yield break;
        }

        var verb = DryRun ? "would be" : "were";
        yield return $"{collectionName}{(DryRun ? " (dry run, nothing changed)" : "")}:";
        foreach (var line in Section($"{Repointed.Count} mods {verb} pointed at their own download", Repointed)) yield return line;
        foreach (var line in Section($"{Removed.Count} old versions {verb} removed (their replacement is installed)", Removed)) yield return line;
        foreach (var line in Section($"{Dropped.Count} mods {verb} moved to My Mods", Dropped.Select(static mod => mod.Name))) yield return line;
        foreach (var line in Section($"{SettingsChanged.Count} mods have changed install settings; reinstall them from the collection page", SettingsChanged)) yield return line;
        foreach (var line in Section($"{SkippedRules.Count} of your rules couldn't be carried over", SkippedRules)) yield return line;
    }

    private static IEnumerable<string> Section(string heading, IEnumerable<string> names)
    {
        yield return "  " + heading;
        foreach (var name in names) yield return "    " + name;
    }
}

/// <summary>
/// The update was refused before the collection was switched. The repair that runs first may already have changed the
/// collection, so its result comes along: its dropped mods are in My Mods and the user still decides about them.
/// </summary>
public sealed class CollectionUpdateRefusedException(string message, CollectionRepairResult repaired, Exception inner) : UserFacingException(message, inner)
{
    public override string Title => "The collection wasn't updated";

    /// <summary>What the repair before the refusal did.</summary>
    public CollectionRepairResult Repaired { get; } = repaired;
}

public static partial class CollectionUpdater
{
    /// <summary>
    /// Makes every mod in the collection match the revision the collection is on, judged by the file each mod was
    /// installed from: a mod whose entry names the wrong download is pointed at its own, a proven old version whose one
    /// replacement is installed is removed, and every other mod the revision doesn't list moves to the loadout's own collection.
    /// </summary>
    /// <remarks>
    /// Only the collection's own mods are looked at, so the user's mods and other collections are never touched. A mod
    /// whose file can't be told (its library item is gone) is left as it is. Running it on a healthy collection changes
    /// nothing. Install settings can't be fixed this way; mods whose settings changed are listed in the result.
    /// </remarks>
    /// <param name="dryRun">Work out and return the repair without changing anything.</param>
    /// <exception cref="InvalidOperationException">The collection is being installed or updated right now. Nothing was changed.</exception>
    public static async ValueTask<CollectionRepairResult> Repair(IServiceProvider serviceProvider, NexusCollectionLoadoutGroup.ReadOnly group, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var key = (group.Collection.Id, group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId);

        await Gate.WaitAsync(cancellationToken);
        // Reported as an update, so an open page keeps its install actions off until the repair is done.
        SetUpdating(key, true);
        try
        {
            group = NexusCollectionLoadoutGroup.Load(serviceProvider.GetRequiredService<IConnection>().Db, group.Id);
            if (group.IsValid() is false) throw new InvalidOperationException("The collection is no longer installed in this loadout.");
            ThrowIfInstalling(serviceProvider.GetRequiredService<IJobMonitor>(), group);
            return await RepairUnlocked(serviceProvider, group, dryRun, cancellationToken);
        }
        finally
        {
            SetUpdating(key, false);
            Gate.Release();
        }
    }

    private static void ThrowIfInstalling(IJobMonitor jobMonitor, NexusCollectionLoadoutGroup.ReadOnly group)
    {
        if (jobMonitor.Jobs.Any(job => job.Status is JobStatus.Created or JobStatus.Running or JobStatus.Paused && Blocks(job.Definition, group)))
            throw new InvalidOperationException("The collection is still being installed. Wait for it to finish, or cancel it, then try again.");
    }

    /// <summary><see cref="Repair"/> for a caller that already holds the gate.</summary>
    private static async ValueTask<CollectionRepairResult> RepairUnlocked(IServiceProvider serviceProvider, NexusCollectionLoadoutGroup.ReadOnly group, bool dryRun, CancellationToken cancellationToken)
    {
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var downloader = serviceProvider.GetRequiredService<CollectionDownloader>();
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CollectionUpdater));
        var db = connection.Db;
        group = NexusCollectionLoadoutGroup.Load(db, group.Id);
        var revision = group.Revision;
        var libraryFile = group.LibraryFile;
        var loadoutId = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;

        var downloadsByFile = revision.Downloads
            .OrderBy(static download => download.ArrayIndex)
            .GroupBy(download => FileOf(download, libraryFile))
            .ToDictionary(static files => files.Key, static files => files.ToArray());

        var items = group.GetCollectionItems().OrderBy(static item => item.Id.Value).ToArray();
        var filesOf = items.ToDictionary(static item => item.Id, item => InstalledFiles(db, item));

        // Mods that name their own file in this revision claim it first, so a stray one can't take it from them.
        var claimed = new HashSet<EntityId>();
        var correct = new List<NexusCollectionItemLoadoutGroup.ReadOnly>();
        var misplaced = new List<NexusCollectionItemLoadoutGroup.ReadOnly>();
        foreach (var item in items)
        {
            var files = filesOf[item.Id];
            if (files.Count == 0) continue;
            var fits = item.Download.IsValid()
                && item.Download.CollectionRevision.Id == revision.Id
                && files.Contains(FileOf(item.Download, libraryFile));
            if (fits && claimed.Add(item.Download.Id)) correct.Add(item);
            else misplaced.Add(item);
        }

        var repoint = new List<(NexusCollectionItemLoadoutGroup.ReadOnly Item, CollectionDownload.ReadOnly Download)>();
        var stale = new List<NexusCollectionItemLoadoutGroup.ReadOnly>();
        foreach (var item in misplaced)
        {
            var own = UnclaimedDownload(filesOf[item.Id], downloadsByFile, claimed);
            if (own is { } download)
            {
                claimed.Add(download.Id);
                repoint.Add((item, download));
            }
            else
            {
                stale.Add(item);
            }
        }

        logger.LogDebug("Checked the {Count} mods of collection `{Name}` against revision {Revision}: {Correct} match, {Repoint} name the wrong download, {Stale} aren't in the revision",
            items.Length, revision.Collection.Name, revision.RevisionNumber, correct.Count, repoint.Count, stale.Count);

        // A collection that needs no repair isn't checked for changed settings either: after a normal update, a mod whose
        // choices changed was installed again with the new ones, and its file still matches an older revision's entry.
        if (repoint.Count == 0 && stale.Count == 0) return CollectionRepairResult.Nothing(dryRun);

        var olderFiles = OlderRevisionFiles(group, downloader);
        var (slots, removedNames, dropped) = SortStale(db, stale, correct, repoint, filesOf, olderFiles, libraryFile);

        var settled = correct.Select(static item => (Item: item, Download: item.Download)).Concat(repoint).ToArray();
        var settingsChanged = await ChangedSettings(serviceProvider, group, settled, cancellationToken);

        var repointed = repoint.Select(static pair => pair.Item.AsLoadoutItemGroup().AsLoadoutItem().Name).ToArray();
        if (dryRun) return new CollectionRepairResult(repointed, removedNames, dropped, settingsChanged, [], DryRun: true);

        // The collection's rules were applied through the same wrong entries, so they name the wrong mods. They are
        // deleted while the entries still match them and applied again once the entries are right.
        var misappliedRules = StoredRulesOf(connection, db, group, loadoutId);

        using (var tx = connection.BeginTransaction())
        {
            foreach (var rule in misappliedRules) tx.Delete(rule, recursive: false);

            foreach (var (item, download) in repoint)
            {
                tx.Add(item.Id, NexusCollectionItemLoadoutGroup.Download, download);
                tx.Add(item.Id, NexusCollectionItemLoadoutGroup.IsRequired, download.IsRequired);
                if (NexusCollectionBundledLoadoutGroup.BundleDownload.TryGetValue(NexusCollectionBundledLoadoutGroup.Load(db, item.Id), out _)
                    && download.TryGetAsCollectionDownloadBundled(out var bundled))
                {
                    tx.Add(item.Id, NexusCollectionBundledLoadoutGroup.BundleDownload, bundled);
                    tx.Add(item.Id, NexusCollectionBundledLoadoutGroup.CollectionLibraryFile, libraryFile);
                }
            }

            // The same move as the update's: out of the collection, into the user's own. Old versions go below, once
            // their rules and slot have moved to the replacement.
            if (stale.Count > 0)
            {
                EntityId userCollection = DefaultCollection.Of(db, loadoutId) is { } existing ? existing.Value : DefaultCollection.Create(tx, loadoutId).Id;
                foreach (var item in stale)
                {
                    CollectionCreator.DetachFromCollection(tx, db, item.Id);
                    tx.Add(item.Id, LoadoutItem.ParentId, userCollection);
                }
            }

            await tx.Commit();
        }

        var (_, carriedOver) = await FinishReplacements(serviceProvider, connection, loadoutId, group.Id, slots);
        var skippedRules = carriedOver.ToList();
        try
        {
            await serviceProvider.GetRequiredService<ILoadoutManager>().ApplyCollectionDownloadRules(NexusCollectionLoadoutGroupId.From(group.Id));
        }
        catch (CollectionRulesCycleException e)
        {
            logger.LogWarning(e, "The rules of collection `{Name}` couldn't be applied again after the repair", revision.Collection.Name);
            skippedRules.Add(e.Message);
        }

        logger.LogInformation("Repaired collection `{Name}` on revision {Revision}: {Repointed} mods pointed at their own download, {Removed} old versions removed, {Dropped} mods moved to My Mods, {Settings} with changed install settings",
            revision.Collection.Name, revision.RevisionNumber, repointed.Length, removedNames.Count, dropped.Count, settingsChanged.Count);
        return new CollectionRepairResult(repointed, removedNames, dropped, settingsChanged, skippedRules, DryRun: false);
    }

    /// <summary>The first download of the revision that is one of <paramref name="files"/> and no other mod has claimed.</summary>
    private static CollectionDownload.ReadOnly? UnclaimedDownload(
        HashSet<string> files, Dictionary<string, CollectionDownload.ReadOnly[]> downloadsByFile, HashSet<EntityId> claimed)
    {
        foreach (var file in files)
        {
            foreach (var download in downloadsByFile.GetValueOrDefault(file) ?? [])
            {
                if (claimed.Contains(download.Id)) continue;
                return download;
            }
        }

        return null;
    }

    /// <summary>
    /// Splits the mods the revision doesn't list into old versions to remove and mods for My Mods. An old version is
    /// removed only when nothing is in doubt: its file is one an older revision of this collection listed, it is the only
    /// stale version of its mod, and the mod has exactly one replacement (a file no older revision listed). Anything
    /// else, such as a mod with several files, goes to My Mods, where the user can still remove it.
    /// </summary>
    private static (List<ReplacedSlot> Slots, List<string> Removed, List<DroppedMod> Dropped) SortStale(
        IDb db,
        IReadOnlyList<NexusCollectionItemLoadoutGroup.ReadOnly> stale,
        IReadOnlyList<NexusCollectionItemLoadoutGroup.ReadOnly> correct,
        IReadOnlyList<(NexusCollectionItemLoadoutGroup.ReadOnly Item, CollectionDownload.ReadOnly Download)> repoint,
        Dictionary<EntityId, HashSet<string>> filesOf,
        HashSet<string> olderFiles,
        NexusModsCollectionLibraryFile.ReadOnly libraryFile)
    {
        var replacements = correct.Select(static item => item.Download)
            .Concat(repoint.Select(static pair => pair.Download))
            .Where(download => olderFiles.Contains(FileOf(download, libraryFile)) is false)
            .GroupBy(download => Identify(download, libraryFile).Mod)
            .ToDictionary(static mods => mods.Key, static mods => mods.ToArray());

        var modOf = stale.ToDictionary(static item => item.Id, item => InstalledModOf(db, item));
        var staleCount = modOf.Values.OfType<string>().GroupBy(static mod => mod).ToDictionary(static mods => mods.Key, static mods => mods.Count());

        var slots = new List<ReplacedSlot>();
        var removed = new List<string>();
        var dropped = new List<DroppedMod>();
        foreach (var item in stale)
        {
            var groupId = item.AsLoadoutItemGroup().LoadoutItemGroupId;
            var name = item.AsLoadoutItemGroup().AsLoadoutItem().Name;
            var isProvenOld = filesOf[item.Id].Overlaps(olderFiles);
            if (modOf[item.Id] is { } mod && isProvenOld && staleCount[mod] == 1
                && replacements.TryGetValue(mod, out var candidates) && candidates.Length == 1)
            {
                slots.Add(new ReplacedSlot(CollectionDownloadId.From(candidates[0].Id), groupId, LoadoutItem.Load(db, item.Id).IsDisabled));
                removed.Add(name);
            }
            else
            {
                dropped.Add(new DroppedMod(groupId, name));
            }
        }

        return (slots, removed, dropped);
    }

    /// <summary>
    /// The mods whose file an older revision listed with other install settings than the current revision does. The
    /// newest older revision that lists the file decides, as the one the mod was most likely installed from.
    /// </summary>
    private static async ValueTask<List<string>> ChangedSettings(
        IServiceProvider serviceProvider,
        NexusCollectionLoadoutGroup.ReadOnly group,
        IReadOnlyList<(NexusCollectionItemLoadoutGroup.ReadOnly Item, CollectionDownload.ReadOnly Download)> settled,
        CancellationToken cancellationToken)
    {
        var library = serviceProvider.GetRequiredService<NexusModsLibrary>();
        var downloader = serviceProvider.GetRequiredService<CollectionDownloader>();
        var options = serviceProvider.GetRequiredService<JsonSerializerOptions>();
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CollectionUpdater));

        var current = await TryParse(library, group.Revision, downloader, logger, cancellationToken);
        if (current is null) return [];

        // Newest first, so the first revision that lists a file is the one that counts.
        var older = new List<(CollectionRevisionMetadata.ReadOnly Revision, NexusModsCollectionLibraryFile.ReadOnly File, CollectionRoot Root)>();
        foreach (var revision in group.Collection.Revisions.Where(r => r.RevisionNumber < group.Revision.RevisionNumber).OrderByDescending(static r => r.RevisionNumber))
        {
            var root = await TryParse(library, revision, downloader, logger, cancellationToken);
            if (root is not null) older.Add((revision, downloader.GetLibraryFile(revision), root));
        }

        var changed = new List<string>();
        foreach (var (item, download) in settled)
        {
            if (download.ArrayIndex >= current.Mods.Length) continue;
            var file = FileOf(download, group.LibraryFile);
            if (OlderSpecOf(older, file, options) is { } olderSpec && olderSpec != SpecOf(current.Mods[download.ArrayIndex], options))
                changed.Add(item.AsLoadoutItemGroup().AsLoadoutItem().Name);
        }

        return changed;
    }

    /// <summary>The install settings the newest of the <paramref name="older"/> revisions that lists <paramref name="file"/> gave it, or null if none does.</summary>
    private static string? OlderSpecOf(
        IEnumerable<(CollectionRevisionMetadata.ReadOnly Revision, NexusModsCollectionLibraryFile.ReadOnly File, CollectionRoot Root)> older,
        string file,
        JsonSerializerOptions options)
    {
        foreach (var (revision, revisionFile, root) in older)
        {
            foreach (var download in revision.Downloads)
            {
                if (download.ArrayIndex >= root.Mods.Length || FileOf(download, revisionFile) != file) continue;
                return SpecOf(root.Mods[download.ArrayIndex], options);
            }
        }

        return null;
    }

    /// <summary>The file a download stands for, in the same terms as <see cref="InstalledFiles"/>.</summary>
    private static string FileOf(CollectionDownload.ReadOnly download, NexusModsCollectionLibraryFile.ReadOnly libraryFile)
    {
        if (download.TryGetAsCollectionDownloadNexusMods(out var nexus)) return $"nexus-file:{nexus.FileUid.AsUlong}";
        if (download.TryGetAsCollectionDownloadExternal(out var external)) return $"md5:{external.Md5}";
        return Identify(download, libraryFile).File;
    }

    /// <summary>The bundled download a bundled mod was installed from, with the collection archive its files came from.</summary>
    private static (CollectionDownload.ReadOnly Download, NexusModsCollectionLibraryFile.ReadOnly File)? BundleOf(IDb db, NexusCollectionItemLoadoutGroup.ReadOnly item)
    {
        var bundledGroup = NexusCollectionBundledLoadoutGroup.Load(db, item.Id);
        if (NexusCollectionBundledLoadoutGroup.BundleDownload.TryGetValue(bundledGroup, out var downloadId) is false) return null;
        if (NexusCollectionBundledLoadoutGroup.CollectionLibraryFile.TryGetValue(bundledGroup, out var fileId) is false) return null;

        var download = CollectionDownload.Load(db, downloadId);
        var file = NexusModsCollectionLibraryFile.Load(db, fileId);
        return download.IsValid() && file.IsValid() ? (download, file) : null;
    }

    /// <summary>
    /// Every way the file an installed mod came from can be named: a library item can be both a Nexus file and a file
    /// with an md5, and the collection may list it either way. Empty when the library item is gone.
    /// </summary>
    private static HashSet<string> InstalledFiles(IDb db, NexusCollectionItemLoadoutGroup.ReadOnly item)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        if (BundleOf(db, item) is (var bundle, var bundleFile)) files.Add(Identify(bundle, bundleFile).File);

        var linked = LibraryLinkedLoadoutItem.Load(db, item.Id);
        if (linked.IsValid() is false) return files;

        var nexusItem = NexusModsLibraryItem.Load(db, linked.LibraryItemId);
        if (nexusItem.IsValid() && nexusItem.FileMetadata.IsValid()) files.Add($"nexus-file:{nexusItem.FileMetadata.Uid.AsUlong}");

        var libraryFile = LibraryFile.Load(db, linked.LibraryItemId);
        if (libraryFile.IsValid() && LibraryFile.Md5.TryGetValue(libraryFile, out var md5)) files.Add($"md5:{md5}");

        return files;
    }

    /// <summary>
    /// The mod an installed item is a version of, in the terms of <see cref="Identify"/>, from what the item itself was
    /// installed from and never from its collection entry, which may be the wrong one: a Nexus file's mod page, or a
    /// bundled mod's own bundle. Null for anything else (an external download says nothing about which mod it is), which
    /// sends the item to My Mods.
    /// </summary>
    private static string? InstalledModOf(IDb db, NexusCollectionItemLoadoutGroup.ReadOnly item)
    {
        if (BundleOf(db, item) is (var bundle, var bundleFile)) return Identify(bundle, bundleFile).Mod;

        var linked = LibraryLinkedLoadoutItem.Load(db, item.Id);
        if (linked.IsValid() is false) return null;

        var nexusItem = NexusModsLibraryItem.Load(db, linked.LibraryItemId);
        if (nexusItem.IsValid() && nexusItem.FileMetadata.IsValid() && nexusItem.FileMetadata.ModPage.IsValid())
            return $"nexus:{nexusItem.FileMetadata.ModPage.Uid.AsUlong}";

        return null;
    }

    /// <summary>The files, in the terms of <see cref="FileOf"/>, that the collection's revisions older than the group's listed.</summary>
    private static HashSet<string> OlderRevisionFiles(NexusCollectionLoadoutGroup.ReadOnly group, CollectionDownloader downloader)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var older in group.Collection.Revisions.Where(revision => revision.RevisionNumber < group.Revision.RevisionNumber))
        {
            // A bundled mod's files are part of its revision's archive. Without the archive only the other kinds count.
            NexusModsCollectionLibraryFile.ReadOnly? archive;
            try
            {
                archive = downloader.GetLibraryFile(older);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                archive = null;
            }

            foreach (var download in older.Downloads)
            {
                if (download.TryGetAsCollectionDownloadBundled(out _))
                {
                    if (archive is { } bundleArchive) files.Add(FileOf(download, bundleArchive));
                    continue;
                }

                files.Add(FileOf(download, default));
            }
        }

        return files;
    }
}

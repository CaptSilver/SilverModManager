using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.ExceptionServices;
using DynamicData;
using DynamicData.Kernel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Collections;

/// <summary>A mod the update took out of the collection because the new revision doesn't list it.</summary>
/// <param name="Group">The mod's group. It now sits in the loadout's own collection.</param>
/// <param name="Name">Name to show the user.</param>
public sealed record DroppedMod(LoadoutItemGroupId Group, string Name);

/// <summary>What <see cref="CollectionUpdater.Switch"/> did, and what is left for the install and the clean-up after it.</summary>
/// <param name="Group">The collection's group, now on the new revision.</param>
/// <param name="Revision">The revision the group was switched to.</param>
/// <param name="LibraryFile">The new revision's collection archive.</param>
/// <param name="Plan">How each mod was handled.</param>
/// <param name="Items">The downloads the install has to run for.</param>
/// <param name="Dropped">The mods that are no longer part of the collection.</param>
/// <param name="ReplacedSlots">The old versions of the changed mods, which the update removes once their replacements are installed.</param>
public sealed record CollectionSwitch(
    NexusCollectionLoadoutGroup.ReadOnly Group,
    CollectionRevisionMetadata.ReadOnly Revision,
    NexusModsCollectionLibraryFile.ReadOnly LibraryFile,
    CollectionUpdatePlan Plan,
    CollectionDownload.ReadOnly[] Items,
    IReadOnlyList<DroppedMod> Dropped,
    IReadOnlyList<ReplacedSlot> ReplacedSlots);

/// <summary>An old version of a changed mod, waiting for its replacement. It is a plain mod of the loadout until then.</summary>
/// <param name="NewDownload">The download that replaces the mod.</param>
/// <param name="OldGroup">The old version's group.</param>
/// <param name="WasDisabled">Whether the user had switched the old one off.</param>
public sealed record ReplacedSlot(CollectionDownloadId NewDownload, LoadoutItemGroupId OldGroup, bool WasDisabled);

/// <summary>What <see cref="CollectionUpdater.Update"/> leaves for the caller.</summary>
/// <param name="Group">The collection's group after the install.</param>
/// <param name="Dropped">The mods that are no longer part of the collection; the user decides whether to keep them.</param>
/// <param name="Failed">The mods that failed to install.</param>
/// <param name="SkippedRules">The user's rules that couldn't be carried over to a replacement because they would contradict other rules.</param>
/// <param name="Error">What stopped the install after the group was switched, if anything. The clean-up has run either way.</param>
/// <param name="OldVersionsKept">How many of the failed mods replace an older version, which stays in the loadout as the user's own mod.</param>
public sealed record CollectionUpdateResult(
    NexusCollectionLoadoutGroup.ReadOnly Group,
    IReadOnlyList<DroppedMod> Dropped,
    IReadOnlyList<InstallCollectionJob.FailedItem> Failed,
    int OldVersionsKept,
    IReadOnlyList<string> SkippedRules,
    Exception? Error);

/// <summary>
/// Updates an installed Nexus collection to another revision in place: the collection keeps its group, its place in
/// the menu and the position of every mod that didn't change, instead of the new revision being installed as a second collection.
/// </summary>
public static partial class CollectionUpdater
{
    // The checks only hold if nothing else installs into or removes the group between them and the switch.
    private static readonly SemaphoreSlim Gate = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// The group a revision of <paramref name="collection"/> is installed as in the loadout, whichever revision that is.
    /// A revision's own page asks this to tell "install" from "update".
    /// </summary>
    public static Optional<NexusCollectionLoadoutGroup.ReadOnly> GetInstalledGroup(CollectionMetadata.ReadOnly collection, LoadoutId loadoutId, IDb db)
    {
        foreach (var datom in db.Datoms(NexusCollectionLoadoutGroup.Collection, collection))
        {
            var group = NexusCollectionLoadoutGroup.Load(db, datom.E);
            if (!group.IsValid()) continue;
            if (group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId != loadoutId) continue;
            return group;
        }

        return Optional<NexusCollectionLoadoutGroup.ReadOnly>.None;
    }

    /// <summary>
    /// <see cref="GetInstalledGroup"/>, again whenever a collection is installed, removed or switched to another revision.
    /// </summary>
    public static IObservable<Optional<NexusCollectionLoadoutGroup.ReadOnly>> ObserveInstalledGroup(IConnection connection, CollectionMetadata.ReadOnly collection, LoadoutId loadoutId) =>
        // The revision datom, not the collection one: an update changes only the revision.
        connection.ObserveDatoms(NexusCollectionLoadoutGroup.Revision)
            .QueryWhenChanged(_ => GetInstalledGroup(collection, loadoutId, connection.Db))
            .Prepend(GetInstalledGroup(collection, loadoutId, connection.Db));

    /// <summary>
    /// Emits whether the loadout has <paramref name="revision"/>'s collection installed on a newer revision, which leaves
    /// this revision as a leftover of an earlier update rather than something to install.
    /// </summary>
    public static IObservable<bool> ObserveIsSuperseded(IConnection connection, CollectionRevisionMetadata.ReadOnly revision, LoadoutId loadoutId)
    {
        var number = revision.RevisionNumber;
        return ObserveInstalledGroup(connection, revision.Collection, loadoutId)
            .Select(installed => installed.HasValue && installed.Value.Revision.RevisionNumber > number)
            .DistinctUntilChanged();
    }

    /// <summary>Whether installing <paramref name="target"/> over the installed group is an update, i.e. the revision is newer.</summary>
    public static bool IsUpdateTo(NexusCollectionLoadoutGroup.ReadOnly installed, CollectionRevisionMetadata.ReadOnly target) =>
        target.RevisionNumber > installed.Revision.RevisionNumber;

    private static readonly object UpdatingLock = new();
    private static readonly HashSet<(EntityId Collection, LoadoutId Loadout)> Updating = [];
    private static readonly Subject<Unit> UpdatingChanged = new();

    /// <summary>
    /// Emits true while an update of <paramref name="collection"/> in the loadout is running, from before the group is
    /// switched until the last step, so a page can keep its install actions off for the whole time.
    /// </summary>
    public static IObservable<bool> ObserveIsUpdating(CollectionMetadata.ReadOnly collection, LoadoutId loadoutId)
    {
        var key = (collection.Id, loadoutId);
        bool IsUpdating() { lock (UpdatingLock) return Updating.Contains(key); }
        return UpdatingChanged.StartWith(Unit.Default).Select(_ => IsUpdating()).DistinctUntilChanged();
    }

    private static void SetUpdating((EntityId, LoadoutId) key, bool value)
    {
        lock (UpdatingLock)
        {
            if (value) Updating.Add(key);
            else Updating.Remove(key);
        }

        UpdatingChanged.OnNext(Unit.Default);
    }

    /// <summary>
    /// Whether a running job is installing into the group, so an update must not start: one installing the whole collection
    /// (any revision of it) in the group's loadout, or one installing a single mod into the group.
    /// </summary>
    public static bool Blocks(IJobDefinition job, NexusCollectionLoadoutGroup.ReadOnly group) => job switch
    {
        InstallCollectionJob install => install.TargetLoadout == group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId
            && install.RevisionMetadata.Collection.Id == group.Collection.Id,
        InstallCollectionDownloadJob download => download.Group.Id == group.Id,
        _ => false,
    };

    /// <summary>
    /// Switches the group to <paramref name="newRevision"/>, installs what the new revision adds or changes, and puts
    /// replaced mods where the old ones stood.
    /// </summary>
    /// <remarks>
    /// First runs <see cref="Repair"/>, so a collection an earlier update left inconsistent is sorted out before it is
    /// compared with the new revision. Beyond that, nothing is changed unless every archive the install needs is already in the library. After that there is no
    /// rollback: a mod that fails to install leaves the group on the new revision with that mod not installed, which the
    /// revision's page shows and offers to install again.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The update can't be done right now; the message says why. Nothing was changed.</exception>
    public static async ValueTask<CollectionUpdateResult> Update(
        IServiceProvider serviceProvider,
        NexusCollectionLoadoutGroup.ReadOnly group,
        CollectionRevisionMetadata.ReadOnly newRevision,
        CancellationToken cancellationToken = default)
    {
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var monitor = serviceProvider.GetRequiredService<IJobMonitor>();
        var key = (group.Collection.Id, group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId);

        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CollectionUpdater));

        await Gate.WaitAsync(cancellationToken);
        SetUpdating(key, true);
        try
        {
            group = CheckCanSwitch(connection.Db, monitor, group, newRevision);
            var repaired = await RepairUnlocked(serviceProvider, group, dryRun: false, cancellationToken);

            CollectionSwitch switched;
            try
            {
                switched = await SwitchUnlocked(serviceProvider, group, newRevision, cancellationToken);
            }
            catch (InvalidOperationException e)
            {
                // The repair is already committed; its dropped mods sit in My Mods and the user still gets asked about them.
                throw new CollectionUpdateRefusedException(e.Message, repaired, e);
            }
            var loadoutId = switched.Group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;

            var job = InstallCollectionJob.Create(serviceProvider, loadoutId, switched.LibraryFile, newRevision, switched.Items, preserveEnabledState: true);
            using var cancelJob = cancellationToken.Register(() => monitor.Cancel(job));

            Exception? failure = null;
            try
            {
                await job;
            }
            catch (Exception e)
            {
                failure = e;
            }

            // The install can end early (cancelled, or the collection's rules contradict the user's after every mod went in), so
            // the clean-up works from what is in the database: an old version goes only if its replacement is there.
            var (kept, skippedRules) = await FinishReplacements(serviceProvider, connection, loadoutId, switched.Group.Id, switched.ReplacedSlots);
            logger.LogDebug("Update of collection `{Slug}` to revision {To} installed {Installed} mods ({Failed} failed); {Replaced} old versions replaced, {Kept} kept as the user's own",
                newRevision.Collection.Slug, newRevision.RevisionNumber, switched.Items.Length - job.JobDefinition.FailedItems.Count, job.JobDefinition.FailedItems.Count,
                switched.ReplacedSlots.Count - kept, kept);

            if (failure is not null && (cancellationToken.IsCancellationRequested || TimeoutDetection.IsRealCancellation(failure)))
                ExceptionDispatchInfo.Capture(failure).Throw();

            return new CollectionUpdateResult(
                NexusCollectionLoadoutGroup.Load(connection.Db, switched.Group.Id),
                [.. repaired.Dropped, .. switched.Dropped],
                job.JobDefinition.FailedItems,
                kept,
                [.. repaired.SkippedRules, .. skippedRules],
                failure);
        }
        finally
        {
            SetUpdating(key, false);
            Gate.Release();
        }
    }

    /// <summary>
    /// The first half of <see cref="Update"/>: points the group at the new revision, and sorts the installed mods into
    /// the ones that stay (repointed at the new revision's downloads), the ones the new revision replaces (taken out of the collection until the replacement is installed) and
    /// the ones it no longer lists (moved to the loadout's own collection). No mod is installed.
    /// </summary>
    public static ValueTask<CollectionSwitch> Switch(
        IServiceProvider serviceProvider,
        NexusCollectionLoadoutGroup.ReadOnly group,
        CollectionRevisionMetadata.ReadOnly newRevision,
        CancellationToken cancellationToken = default)
    {
        group = CheckCanSwitch(serviceProvider.GetRequiredService<IConnection>().Db, serviceProvider.GetRequiredService<IJobMonitor>(), group, newRevision);
        return SwitchUnlocked(serviceProvider, group, newRevision, cancellationToken);
    }

    /// <summary>The group as it is now, if it can be switched to <paramref name="newRevision"/>.</summary>
    /// <exception cref="InvalidOperationException">It can't; the message says why.</exception>
    private static NexusCollectionLoadoutGroup.ReadOnly CheckCanSwitch(IDb db, IJobMonitor jobMonitor, NexusCollectionLoadoutGroup.ReadOnly group, CollectionRevisionMetadata.ReadOnly newRevision)
    {
        group = NexusCollectionLoadoutGroup.Load(db, group.Id);
        if (!group.IsValid()) throw new InvalidOperationException("The collection is no longer installed in this loadout.");
        if (group.Collection.Id != newRevision.Collection.Id) throw new InvalidOperationException("That revision belongs to another collection.");
        if (group.Revision.Id == newRevision.Id) throw new InvalidOperationException("The collection is already on that revision.");
        if (!IsUpdateTo(group, newRevision))
            throw new InvalidOperationException($"Revision {newRevision.RevisionNumber} is older than the installed revision {group.Revision.RevisionNumber}. Updating to an older revision isn't supported.");
        ThrowIfInstalling(jobMonitor, group);
        return group;
    }

    private static async ValueTask<CollectionSwitch> SwitchUnlocked(
        IServiceProvider serviceProvider,
        NexusCollectionLoadoutGroup.ReadOnly group,
        CollectionRevisionMetadata.ReadOnly newRevision,
        CancellationToken cancellationToken)
    {
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var downloader = serviceProvider.GetRequiredService<CollectionDownloader>();
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CollectionUpdater));

        var db = connection.Db;
        group = NexusCollectionLoadoutGroup.Load(db, group.Id);
        var loadoutId = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;

        var nexusModsLibrary = serviceProvider.GetRequiredService<NexusModsLibrary>();
        var options = serviceProvider.GetRequiredService<JsonSerializerOptions>();
        var newLibraryFile = downloader.GetLibraryFile(newRevision);
        var newRoot = await nexusModsLibrary.ParseCollectionJsonFile(newLibraryFile, cancellationToken);
        var oldRoot = await TryParse(nexusModsLibrary, group.Revision, downloader, logger, cancellationToken);

        var items = group.GetCollectionItems().ToArray();
        var installed = items.Select(item => Describe(item, oldRoot, group.LibraryFile, options)).ToArray();
        var newDownloads = newRevision.Downloads.OrderBy(static download => download.ArrayIndex).ToArray();
        var available = newDownloads.Select(download => Describe(download, newRoot, newLibraryFile, options)).ToArray();

        var plan = CollectionUpdatePlanner.Plan(installed, available);
        logger.LogDebug("Plan for collection `{Slug}` from revision {From} ({Installed} mods) to {To} ({Available} mods): {Kept} unchanged, {Changed} changed, {Added} new ({AddedRequired} required), {Dropped} dropped",
            newRevision.Collection.Slug, group.Revision.RevisionNumber, installed.Length, newRevision.RevisionNumber, available.Length,
            plan.Kept.Count, plan.Changed.Count, plan.Added.Count, plan.Added.Count(static mod => mod.IsRequired), plan.Dropped.Count);

        var toInstall = plan.ToInstall.Select(mod => mod.Download).ToHashSet();
        var itemsToInstall = newDownloads.Where(download => toInstall.Contains(CollectionDownloadId.From(download.Id))).ToArray();
        var missing = itemsToInstall.Where(download => !CollectionDownloader.GetStatus(download, db).IsDownloaded()).Select(static download => download.Name).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"These mods aren't in the library yet: {string.Join(", ", missing)}. Download them from the collection page first.");

        // What the old revision's rules put in the stored rules has to go before the items stop pointing at it.
        var oldRules = StoredRulesOf(connection, db, group, loadoutId);

        var itemByGroup = items.ToDictionary(static item => item.AsLoadoutItemGroup().LoadoutItemGroupId);

        var replaced = plan.Changed
            .Select(change => new ReplacedSlot(
                change.Available.Download,
                change.Installed.Group,
                LoadoutItem.Load(db, change.Installed.Group).IsDisabled))
            .ToArray();

        using (var tx = connection.BeginTransaction())
        {
            tx.Add(group.Id, NexusCollectionLoadoutGroup.Revision, newRevision);
            tx.Add(group.Id, NexusCollectionLoadoutGroup.LibraryFile, newLibraryFile);

            foreach (var kept in plan.Kept)
            {
                var item = itemByGroup[kept.Installed.Group];
                var download = newRevision.Downloads.First(d => CollectionDownloadId.From(d.Id) == kept.Available.Download);
                tx.Add(item.Id, NexusCollectionItemLoadoutGroup.Download, download);
                tx.Add(item.Id, NexusCollectionItemLoadoutGroup.IsRequired, kept.Available.IsRequired);
                if (NexusCollectionBundledLoadoutGroup.BundleDownload.TryGetValue(NexusCollectionBundledLoadoutGroup.Load(db, item.Id), out _)
                    && download.TryGetAsCollectionDownloadBundled(out var bundled))
                {
                    tx.Add(item.Id, NexusCollectionBundledLoadoutGroup.BundleDownload, bundled);
                    tx.Add(item.Id, NexusCollectionBundledLoadoutGroup.CollectionLibraryFile, newLibraryFile);
                }
            }

            // Both stop belonging to the collection now: the install must not take an old version for the new one, and a
            // dropped mod is the user's. An old version of a changed mod goes once its replacement is installed.
            var leaving = plan.Changed.Select(static change => change.Installed.Group).Concat(plan.Dropped.Select(static mod => mod.Group)).ToArray();
            if (leaving.Length > 0)
            {
                EntityId userCollection = DefaultCollection.Of(db, loadoutId) is { } existing ? existing.Value : DefaultCollection.Create(tx, loadoutId).Id;
                foreach (var leaver in leaving)
                {
                    CollectionCreator.DetachFromCollection(tx, db, itemByGroup[leaver].Id);
                    tx.Add(itemByGroup[leaver].Id, LoadoutItem.ParentId, userCollection);
                }
            }

            foreach (var rule in oldRules) tx.Delete(rule, recursive: false);

            await tx.Commit();
        }

        var dropped = plan.Dropped.Select(mod => new DroppedMod(mod.Group, mod.Name)).ToArray();
        return new CollectionSwitch(NexusCollectionLoadoutGroup.Load(connection.Db, group.Id), newRevision, newLibraryFile, plan, itemsToInstall, dropped, replaced);
    }

    /// <summary>
    /// Removes the dropped mods the user chose to remove. The others stay in the loadout's own collection, which
    /// <see cref="Switch"/> already moved them to.
    /// </summary>
    public static async ValueTask RemoveDropped(IServiceProvider serviceProvider, IEnumerable<DroppedMod> mods)
    {
        var groups = mods.Select(static mod => mod.Group).ToArray();
        await RemoveGroups(serviceProvider.GetRequiredService<IConnection>(), serviceProvider.GetRequiredService<ILoadoutManager>(), groups);
    }

    /// <summary>
    /// Removes the groups and, for a bundled mod that was taken out of its collection, the archive of its files that
    /// <see cref="CollectionCreator.DetachFromCollection"/> made, once nothing else uses it.
    /// </summary>
    private static async ValueTask RemoveGroups(IConnection connection, ILoadoutManager loadoutManager, LoadoutItemGroupId[] groups)
    {
        var db = connection.Db;
        var archives = groups
            .Select(group => LibraryLinkedLoadoutItem.Load(db, group))
            .Where(static linked => linked.IsValid())
            .Select(static linked => linked.LibraryItemId)
            .Where(archive => ManuallyCreatedArchive.Load(db, archive) is { } created && created.IsValid() && created.Source == ManuallyCreatedArchive.CreationSource.CollectionBundled)
            .ToArray();

        await loadoutManager.RemoveItems(groups);

        using var tx = connection.BeginTransaction();
        foreach (var archive in archives)
        {
            if (connection.Db.Datoms(LibraryLinkedLoadoutItem.LibraryItemId, archive).Count > 0) continue;
            tx.Delete(archive, recursive: true);
        }

        await tx.Commit();
    }

    /// <summary>
    /// For every replaced mod that did get installed: gives the new one the old one's switch-off, rules and slot, then removes the
    /// old one. An old version whose replacement is missing stays, as the user's own mod. Safe to run after a failed or
    /// interrupted install, and again.
    /// </summary>
    /// <remarks>
    /// The removal comes before the reorder on purpose: a crash in between leaves every mod once but not yet in its slot, where
    /// the other way round it would leave both versions of a mod switched on.
    /// </remarks>
    private static async ValueTask<(int Kept, IReadOnlyList<string> SkippedRules)> FinishReplacements(
        IServiceProvider serviceProvider, IConnection connection, LoadoutId loadoutId,
        EntityId group, IReadOnlyList<ReplacedSlot> slots)
    {
        if (slots.Count == 0) return (0, []);
        var loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();

        var installedBy = new Dictionary<CollectionDownloadId, LoadoutItemGroupId>();
        foreach (var item in NexusCollectionLoadoutGroup.Load(connection.Db, group).GetCollectionItems())
            installedBy[CollectionDownloadId.From(item.Download.Id)] = item.AsLoadoutItemGroup().LoadoutItemGroupId;

        var newOf = new Dictionary<LoadoutItemGroupId, LoadoutItemGroupId>();
        foreach (var slot in slots)
        {
            if (installedBy.TryGetValue(slot.NewDownload, out var replacement)) newOf[slot.OldGroup] = replacement;
        }

        if (newOf.Count == 0) return (slots.Count, []);

        // The sort orders learn about the new mods from a background subscription; the order below can only seed rows that exist.
        await Loadout.Load(connection.Db, loadoutId).InstallationInstance.GetGame().SortOrderManager.UpdateLoadOrders(loadoutId);

        // One transaction for the switch-offs.
        using (var tx = connection.BeginTransaction())
        {
            foreach (var slot in slots.Where(slot => slot.WasDisabled && newOf.ContainsKey(slot.OldGroup)))
                tx.Add(newOf[slot.OldGroup].Value, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }

        var skipped = await CarryRulesOver(loadoutManager, connection, loadoutId, newOf);
        if (skipped.Count > 0)
            serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CollectionUpdater)).LogWarning("Rules not carried over to the updated mods because they would contradict other rules: {Rules}", string.Join("; ", skipped));

        // Where every group stands once each new one is in its old one's place. A new group that has no row yet is placed at its listed spot.
        var replacements = newOf.Values.ToHashSet();
        var order = LoadoutItemGroupPriority.FindByLoadout(connection.Db, loadoutId)
            .Where(static row => row.Target.IsValid())
            .OrderByDescending(static row => row.Priority)
            .Select(static row => row.TargetId)
            .ToArray();
        var wanted = new List<LoadoutItemGroupId>(order.Length);
        foreach (var existing in order)
        {
            if (replacements.Contains(existing)) continue;
            wanted.Add(newOf.GetValueOrDefault(existing, existing));
        }

        await RemoveGroups(connection, loadoutManager, newOf.Keys.ToArray());

        using (var tx = connection.BeginTransaction())
        {
            loadoutManager.SetGroupOrder(loadoutId, wanted.Where(group => !newOf.ContainsKey(group)).ToArray(), tx);
            await tx.Commit();
        }

        return (slots.Count - newOf.Count, skipped);
    }

    /// <summary>
    /// Re-creates the rules that name an old version on its replacement. Removing a group deletes the rules that name it, and
    /// by now the only ones left are the user's: the old revision's own were deleted with the switch.
    /// </summary>
    /// <returns>The rules that would contradict others on the replacement, described for the user. They are not carried.</returns>
    private static async ValueTask<IReadOnlyList<string>> CarryRulesOver(ILoadoutManager loadoutManager, IConnection connection, LoadoutId loadoutId, Dictionary<LoadoutItemGroupId, LoadoutItemGroupId> newOf)
    {
        var skipped = new List<string>();
        var rules = LoadoutGroupRule.FindByLoadout(connection.Db, loadoutId).ToArray();
        var existing = rules.Select(static rule => (rule.SourceId, rule.OtherId, rule.Kind)).ToHashSet();
        foreach (var rule in rules.Where(rule => newOf.ContainsKey(rule.SourceId) || newOf.ContainsKey(rule.OtherId)))
        {
            var carried = (Source: newOf.GetValueOrDefault(rule.SourceId, rule.SourceId), Other: newOf.GetValueOrDefault(rule.OtherId, rule.OtherId), rule.Kind);
            if (carried.Source == carried.Other || !existing.Add(carried)) continue;

            var result = await loadoutManager.AddGroupRule(loadoutId, carried.Source, carried.Other, carried.Kind);
            if (!result.IsCycle) continue;

            var (winner, loser) = carried.Kind == GroupRuleKind.After ? (carried.Source, carried.Other) : (carried.Other, carried.Source);
            var text = $"{LoadoutItem.Load(connection.Db, winner).Name} wins over {LoadoutItem.Load(connection.Db, loser).Name}";
            skipped.Add(text);
        }

        return skipped;
    }

    /// <summary>The rows in the loadout that the group's current revision wrote: its download rules applied to the installed mods.</summary>
    private static List<EntityId> StoredRulesOf(IConnection connection, IDb db, NexusCollectionLoadoutGroup.ReadOnly group, LoadoutId loadoutId)
    {
        var applied = connection.Query<(EntityId SourceId, EntityId OtherId, int RuleType)>(
            $"""
             SELECT Source, Other, RuleType FROM loadouts.CollectionRulesOnItems({connection})
             WHERE Parent = {group.Id.Value};
             """
        ).Select(static rule => (
            Source: LoadoutItemGroupId.From(rule.SourceId.Value),
            Other: LoadoutItemGroupId.From(rule.OtherId.Value),
            Kind: (CollectionDownloadRuleType)rule.RuleType switch
            {
                CollectionDownloadRuleType.Before => GroupRuleKind.Before,
                CollectionDownloadRuleType.After => GroupRuleKind.After,
                _ => throw new UnreachableException(),
            })).ToHashSet();

        return LoadoutGroupRule.FindByLoadout(db, loadoutId)
            .Where(rule => applied.Contains((rule.SourceId, rule.OtherId, rule.Kind)))
            .Select(static rule => rule.Id)
            .ToList();
    }

    /// <summary>The revision's collection.json, or null when its archive isn't in the library any more.</summary>
    private static async ValueTask<CollectionRoot?> TryParse(NexusModsLibrary library, CollectionRevisionMetadata.ReadOnly revision, CollectionDownloader downloader, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await library.ParseCollectionJsonFile(downloader.GetLibraryFile(revision), cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is not OperationCanceledException)
        {
            // The old archive can be removed from the library while the collection stays installed. Without it the
            // install settings of the installed mods are unknown, so the file alone decides whether a mod is unchanged.
            logger.LogDebug(e, "Couldn't read the collection.json of revision {Revision} of `{Slug}`; comparing its mods by file only", revision.RevisionNumber, revision.Collection.Slug);
            return null;
        }
    }

    private static InstalledMod Describe(NexusCollectionItemLoadoutGroup.ReadOnly item, CollectionRoot? root, NexusModsCollectionLibraryFile.ReadOnly libraryFile, JsonSerializerOptions options)
    {
        var download = item.Download;
        var group = item.AsLoadoutItemGroup();
        var (file, mod) = Identify(download, libraryFile);
        var spec = root is not null && download.ArrayIndex < root.Mods.Length
            ? SpecOf(root.Mods[download.ArrayIndex], options)
            : null;
        return new InstalledMod(group.LoadoutItemGroupId, group.AsLoadoutItem().Name, file, mod, spec, item.IsRequired);
    }

    private static AvailableMod Describe(CollectionDownload.ReadOnly download, CollectionRoot root, NexusModsCollectionLibraryFile.ReadOnly libraryFile, JsonSerializerOptions options)
    {
        var (file, mod) = Identify(download, libraryFile);
        return new AvailableMod(CollectionDownloadId.From(download.Id), download.Name, file, mod, SpecOf(root.Mods[download.ArrayIndex], options), download.IsRequired);
    }

    /// <summary>The exact file a download is, and the mod it belongs to.</summary>
    private static (string File, string Mod) Identify(CollectionDownload.ReadOnly download, NexusModsCollectionLibraryFile.ReadOnly libraryFile)
    {
        if (download.TryGetAsCollectionDownloadNexusMods(out var nexus))
            return ($"nexus:{nexus.ModUid.AsUlong}:{nexus.FileUid.AsUlong}", $"nexus:{nexus.ModUid.AsUlong}");

        if (download.TryGetAsCollectionDownloadBundled(out var bundled))
            return ($"bundled:{bundled.BundledPath}:{BundledContent(libraryFile, bundled.BundledPath)}", $"bundled:{bundled.BundledPath}");

        if (download.TryGetAsCollectionDownloadExternal(out var external))
            return ($"md5:{external.Md5}", $"uri:{external.Uri}");

        throw new NotSupportedException($"Download `{download.Name}` is of an unknown kind");
    }

    /// <summary>The files under <c>bundled/&lt;path&gt;</c> in the collection archive, so a bundled mod whose files changed is told apart.</summary>
    private static string BundledContent(NexusModsCollectionLibraryFile.ReadOnly collectionFile, RelativePath bundledPath)
    {
        if (!collectionFile.AsLibraryFile().TryGetAsLibraryArchive(out var archive)) return "no archive";
        var prefix = RelativePath.FromUnsanitizedInput("bundled").Join(bundledPath);
        return string.Join(";", archive.Children
            .Where(child => child.Path.InFolder(prefix))
            .Select(child => $"{child.Path.RelativeTo(prefix)}={child.AsLibraryFile().Hash}")
            .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// What the collection says about installing the mod, apart from which file it is: saved fomod choices, patches, and the
    /// list of files to place. Serialized so the pieces compare structurally; patches are sorted since a dictionary has no order.
    /// </summary>
    public static string SpecOf(Mod mod, JsonSerializerOptions options) => JsonSerializer.Serialize(new
    {
        mod.Choices,
        Patches = mod.Patches.OrderBy(static pair => pair.Key.ToString(), StringComparer.Ordinal).ToArray(),
        Hashes = mod.Hashes.OrderBy(static pair => pair.Path.ToString(), StringComparer.Ordinal).ThenBy(static pair => pair.MD5.ToString(), StringComparer.Ordinal).ToArray(),
    }, options);
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.App.UI.Pages.Collections.Update;

using CollectionDownloadEntity = SilverModManager.Abstractions.NexusModsLibrary.Models.CollectionDownload;

/// <summary>
/// An installed Nexus collection moving to a newer revision, behind the shared update flow. The work stays in
/// <see cref="CollectionUpdater"/> and <see cref="CollectionDownloader"/>; this maps them onto the neutral review and
/// outcome the flow shows. The target is a revision number, resolved against the library when the flow asks for the
/// review, so offering the update never touches the network.
/// </summary>
public sealed class NexusCollectionUpdate : ICollectionUpdate
{
    private readonly IServiceProvider _services;
    private readonly IConnection _connection;
    private readonly EntityId _groupId;
    private readonly RevisionNumber _target;

    public NexusCollectionUpdate(IServiceProvider services, NexusCollectionLoadoutGroup.ReadOnly group, RevisionNumber target)
    {
        _services = services;
        _connection = services.GetRequiredService<IConnection>();
        _target = target;
        _groupId = group.Id;
        var current = Group();
        Name = current.Collection.Name;
        CurrentVersionText = VersionText(current.Revision.RevisionNumber);
        NewVersionText = VersionText(target);
    }

    /// <summary>How a revision is named wherever the user sees one.</summary>
    public static string VersionText(RevisionNumber revision) => $"Revision {revision.Value}";

    public string Name { get; }
    public string CurrentVersionText { get; }
    public string NewVersionText { get; }
    public string? Note => null;

    /// <summary>The installed group as the database holds it now; an update by another route may have moved it since the offer was made.</summary>
    private NexusCollectionLoadoutGroup.ReadOnly Group() => NexusCollectionLoadoutGroup.Load(_connection.Db, _groupId);

    /// <summary>
    /// The target revision as the library holds it. A revision the library lacks is fetched first, which needs the
    /// user logged in with Premium.
    /// </summary>
    /// <exception cref="CollectionRevisionUnavailableException">The revision isn't in the library and can't be fetched.</exception>
    private async Task<CollectionRevisionMetadata.ReadOnly> Target(CancellationToken ct)
    {
        var group = Group();
        var collection = group.Collection.Id;
        foreach (var revision in CollectionRevisionMetadata.FindByRevisionNumber(_connection.Db, _target))
        {
            if (revision.Collection.Id == collection) return revision;
        }

        var canDownload = await CollectionDownloadGate.EnsureCanDownloadCollections(
            _services.GetRequiredService<ILoginManager>(),
            _services.GetRequiredService<IWindowManager>(),
            _services.GetRequiredService<IOSInterop>(),
            ct);
        if (!canDownload) throw new CollectionRevisionUnavailableException(Name, NewVersionText);

        var loadout = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;
        return await _services.GetRequiredService<CollectionDownloader>().GetOrAddRevision(group.Collection.Slug, _target, loadout, ct);
    }

    public async Task<CollectionUpdateReview> Review(IProgress<string>? progress, CancellationToken ct)
    {
        var target = await Target(ct);
        var group = Group();

        // A dry run: it names what the repair before the update would move out of the collection, and writes nothing.
        var repaired = await CollectionUpdater.Repair(_services, group, dryRun: true, ct);
        var plan = await CollectionUpdater.PlanSwitch(_services, group, target, ct);

        var db = _connection.Db;
        var downloads = plan.ToInstall
            .Where(download => CollectionDownloader.GetStatus(download, db).IsNotDownloaded())
            .Select(ArchiveOf)
            .ToArray();

        return new CollectionUpdateReview(
            Downloads: downloads,
            DownloadSize: Size.From((ulong)downloads.Sum(static archive => (long)archive.Size.Value)),
            NewBytesUpperBound: null,
            DiskNote: string.Empty,
            Added: [.. plan.Plan.Added.Where(static mod => mod.IsRequired).Select(static mod => mod.Name)],
            Changed: [.. plan.Plan.Changed.Select(static change => change.Available.Name)],
            Renamed: [],
            Dropped: [.. plan.Plan.Dropped.Select(static mod => mod.Name).Concat(repaired.Dropped.Select(static mod => mod.Name)).Distinct()],
            ModOrderChanges: [],
            PluginChanges: [],
            UserEditsKept: [],
            GameFilesKept: [],
            Warnings: [],
            WritesRestorePoint: false,
            DroppedAreRemovedNotAsked: false);
    }

    private static ReviewArchive ArchiveOf(CollectionDownloadEntity.ReadOnly download)
    {
        if (download.TryGetAsCollectionDownloadNexusMods(out var nexus))
            return new ReviewArchive(download.Name, SizeOf(nexus.FileMetadata), "Nexus Mods", NeedsBrowser: false);

        if (download.TryGetAsCollectionDownloadExternal(out var external))
            return new ReviewArchive(download.Name, external.Size, external.Uri.Host, NeedsBrowser: false);

        return new ReviewArchive(download.Name, Size.Zero, "Bundled", NeedsBrowser: false);
    }

    private static Size SizeOf(NexusModsFileMetadata.ReadOnly file) => file.Size is { HasValue: true } size ? size.Value : Size.Zero;

    public async Task<bool> IsDownloaded(CancellationToken ct) =>
        CollectionDownloader.IsFullyDownloaded(CollectionDownloader.GetItems(await Target(ct), CollectionDownloader.ItemType.Required), _connection.Db);

    public async Task<CollectionUpdateDownload> Download(CancellationToken ct)
    {
        var target = await Target(ct);
        var canDownload = await CollectionDownloadGate.EnsureCanDownloadCollections(
            _services.GetRequiredService<ILoginManager>(),
            _services.GetRequiredService<IWindowManager>(),
            _services.GetRequiredService<IOSInterop>(),
            ct);
        if (!canDownload) return CollectionUpdateDownload.CantDownload;

        var loadout = Group().AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;
        await _services.GetRequiredService<CollectionDownloader>().DownloadItems(target, CollectionDownloader.ItemType.Required, _connection.Db, loadout, ct);

        return CollectionDownloader.IsFullyDownloaded(CollectionDownloader.GetItems(target, CollectionDownloader.ItemType.Required), _connection.Db)
            ? CollectionUpdateDownload.Downloaded
            : CollectionUpdateDownload.NotAllDownloaded;
    }

    /// <remarks>
    /// <paramref name="skipMissing"/> is not used: a Nexus update refuses unless every archive it needs is in the
    /// library, and its review never offers to skip any.
    /// </remarks>
    public async Task<CollectionUpdateOutcome> Apply(bool skipMissing, CancellationToken ct)
    {
        var logger = _services.GetRequiredService<ILogger<NexusCollectionUpdate>>();
        try
        {
            var target = await Target(ct);
            var result = await CollectionUpdater.Update(_services, Group(), target, ct);
            logger.LogDebug("Updated collection `{Slug}` to revision {To}: {Failed} failed, {Kept} old versions kept, {Dropped} dropped mods to ask about",
                target.Collection.Slug, target.RevisionNumber, result.Failed.Count, result.OldVersionsKept, result.Dropped.Count);

            return new CollectionUpdateOutcome(
                Updated: result.Error is null,
                Failed: result.Failed.Count,
                Pending: result.Pending.Count,
                DroppedToAsk: result.Dropped,
                Removed: [],
                RestorePoint: null,
                Undo: null,
                Error: result.Error,
                ToastText: CollectionUpdateFlow.ResultText(result, (int)_target.Value),
                Warnings: result.SkippedRules.Count,
                DroppedPromptRevision: (int)_target.Value);
        }
        catch (CollectionUpdateRefusedException e)
        {
            // Refused before the switch, but the repair that runs first may have moved mods out of the collection.
            return Refused(e, e.Repaired.Dropped);
        }
        catch (Exception e) when (!TimeoutDetection.IsRealCancellation(e))
        {
            return Refused(e, []);
        }
    }

    private CollectionUpdateOutcome Refused(Exception error, IReadOnlyList<DroppedMod> droppedToAsk) => new(
        Updated: false,
        Failed: 0,
        Pending: 0,
        DroppedToAsk: droppedToAsk,
        Removed: [],
        RestorePoint: null,
        Undo: null,
        Error: error,
        ToastText: string.Format(Language.ToastNotification_Couldn_t_update_to_revision__0____1_, _target.Value, MarkdownText.Message(error)),
        DroppedPromptRevision: (int)Group().Revision.RevisionNumber.Value);
}

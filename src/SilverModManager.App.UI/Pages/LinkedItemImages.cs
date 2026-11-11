using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using DynamicData;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.App.UI.Controls;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.Sdk.Resources;

namespace SilverModManager.App.UI.Pages;

/// <summary>
/// The thumbnail a loadout row shows for the library item its group is linked to. Every page that draws a linked
/// group asks here, so a source that gains a thumbnail is added once.
/// </summary>
internal static class LinkedItemImages
{
    /// <summary>
    /// The mod-page thumbnail of the library item a group is linked to, or the fallback when the group has no link
    /// or its item has no page. One read; for a row built once.
    /// </summary>
    public static ImageComponent ForGroup(IDb db, IResourceLoader<EntityId, Bitmap> thumbnails, EntityId group)
    {
        return TryForGroup(db, thumbnails, group) ?? new ImageComponent(value: ImagePipelines.ModPageThumbnailFallback);
    }

    /// <summary>
    /// The mod-page thumbnail of the library item a group is linked to, or null when the group has no link or its
    /// item has no Nexus page, for a page that leaves its own icon in place for those.
    /// </summary>
    public static ImageComponent? TryForGroup(IDb db, IResourceLoader<EntityId, Bitmap> thumbnails, EntityId group)
    {
        return PageOf(db, LinkOf(db, group)) is { } page
            ? ImageComponent.FromPipeline(thumbnails, page, ImagePipelines.ModPageThumbnailFallback)
            : null;
    }

    /// <summary>
    /// The same, driven by <paramref name="linkedItem"/> (see <see cref="ObserveLinkedItem"/>), so a row that exists
    /// before its link does updates when the link is written. The lookups and the load start run wherever
    /// <paramref name="linkedItem"/> emits, which is the thread pool.
    /// </summary>
    public static ImageComponent ObserveForGroup(IConnection connection, IResourceLoader<EntityId, Bitmap> thumbnails, IObservable<Optional<EntityId>> linkedItem)
    {
        var fallback = ImagePipelines.ModPageThumbnailFallback;
        var images = linkedItem
            .Select(link => PageOf(connection.Db, link.HasValue ? link.Value : null) is { } page
                ? Observable.FromAsync(ct => thumbnails.LoadResourceAsync(page, ct).AsTask()).Select(static resource => resource.Data)
                : Observable.Return(fallback))
            .Switch();
        return new ImageComponent(fallback, images);
    }

    /// <summary>
    /// The linked library item's id as it changes; none while the group has no link. The connection primes the first
    /// snapshot, and everything downstream runs on the thread pool, not the UI or the database's dispatch thread.
    /// Replays its latest value, so a row's image and action can share one subscription.
    /// </summary>
    public static IObservable<Optional<EntityId>> ObserveLinkedItem(IConnection connection, EntityId group)
    {
        return connection.ObserveDatoms(SliceDescriptor.Create(group))
            .QueryWhenChanged(_ => LinkedItem(connection.Db, group))
            .DistinctUntilChanged()
            .ObserveOn(PoolWithoutDedicatedThreads)
            .Replay(1)
            .RefCount();
    }

    // ObserveOn on the stock pool schedulers parks a dedicated thread per subscription (they offer long-running
    // scheduling); one per visible row is too many.
    private static readonly IScheduler PoolWithoutDedicatedThreads =
        ThreadPoolScheduler.Instance.DisableOptimizations(typeof(ISchedulerLongRunning));

    /// <summary>The library item a group is linked to, if any.</summary>
    internal static Optional<EntityId> LinkedItem(IDb db, EntityId group) =>
        LinkOf(db, group) is { } id ? Optional<EntityId>.Create(id) : Optional<EntityId>.None;

    private static EntityId? LinkOf(IDb db, EntityId group)
    {
        var linked = LibraryLinkedLoadoutItem.Load(db, group);
        return linked.IsValid() && LibraryLinkedLoadoutItem.LibraryItem.TryGetValue(linked, out var item) ? item : null;
    }

    private static EntityId? PageOf(IDb db, EntityId? libraryItem)
    {
        return libraryItem is { } id && NexusModsLibraryItem.TryGet(db, id, out var nexus) ? nexus.Value.ModPageMetadataId : null;
    }
}

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
        return PageOf(db, LinkOf(db, group)) is { } page
            ? ImageComponent.FromPipeline(thumbnails, page, ImagePipelines.ModPageThumbnailFallback)
            : new ImageComponent(value: ImagePipelines.ModPageThumbnailFallback);
    }

    /// <summary>
    /// The same, following the group's <see cref="LibraryLinkedLoadoutItem.LibraryItem"/> datom, so a row that
    /// exists before its link does updates when the link is written.
    /// </summary>
    public static ImageComponent ObserveForGroup(IConnection connection, IResourceLoader<EntityId, Bitmap> thumbnails, EntityId group)
    {
        var fallback = ImagePipelines.ModPageThumbnailFallback;
        var images = ObserveLinkedItem(connection, group)
            .Select(link => PageOf(connection.Db, link.HasValue ? link.Value : null) is { } page
                ? Observable.FromAsync(ct => thumbnails.LoadResourceAsync(page, ct).AsTask()).Select(static resource => resource.Data)
                : Observable.Return(fallback))
            .Switch();
        return new ImageComponent(fallback, images);
    }

    /// <summary>The linked library item's id as it changes; none while the group has no link.</summary>
    public static IObservable<Optional<EntityId>> ObserveLinkedItem(IConnection connection, EntityId group)
    {
        return connection.ObserveDatoms(SliceDescriptor.Create(group))
            .QueryWhenChanged(_ => LinkOf(connection.Db, group))
            .Prepend(LinkOf(connection.Db, group))
            .DistinctUntilChanged()
            .Select(static link => link is { } id ? Optional<EntityId>.Create(id) : Optional<EntityId>.None);
    }

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

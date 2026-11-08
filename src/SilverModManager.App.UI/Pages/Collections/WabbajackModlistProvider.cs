using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.App.UI.Pages.Collections.Update;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Update;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Wabbajack.Models;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// Wabbajack modlists: a group is a <see cref="WabbajackModlistLoadoutGroup"/>, and every modlist in the library that
/// is for the loadout's game and not installed there is pending. A list is listed as installed once its install ran to
/// its end without skipping files, so it is on exactly one side of that line in each loadout.
/// </summary>
public sealed class WabbajackModlistProvider(IServiceProvider services) : ICollectionProvider
{
    public bool Owns(CollectionGroup.ReadOnly group) => group.TryGetAsWabbajackModlistLoadoutGroup(out _);

    public CollectionSource ForGroup(CollectionGroup.ReadOnly group, LoadoutId loadout) =>
        new WabbajackModlistSource(services, loadout, ModlistGroup(group).Modlist);

    public IObservable<bool> IsListedAsInstalled(CollectionGroup.ReadOnly group)
    {
        var modlist = ModlistGroup(group);
        var loadout = group.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;
        return WabbajackModlistInstallState.Observe(services.GetRequiredService<IConnection>(), LibraryFileId.From(modlist.ModlistId.Value), loadout)
            .Select(static install => install.State == WabbajackModlistCardState.Installed)
            .DistinctUntilChanged();
    }

    /// <summary>
    /// An update is offered while the cached gallery listing has a newer release of the list the group is on. It is
    /// read again when the gallery refreshes and when the group changes (the offer goes away once the group is on the
    /// new file). A list with no recorded gallery identity, or one the listing does not contain, has no offer.
    /// </summary>
    public IObservable<ICollectionUpdate?> ObserveUpdate(CollectionGroup.ReadOnly group)
    {
        var id = ModlistGroup(group).Id;
        var connection = services.GetRequiredService<IConnection>();
        var client = services.GetRequiredService<IWabbajackGalleryClient>();

        var listings = client.Refreshed.Select(static listing => (GalleryListing?)listing).StartWith(client.GetCached());
        var groupChanges = connection.ObserveDatoms(id).QueryWhenChanged(static _ => 0).Prepend(0);

        // Assigning a gallery identity writes to the library file, not to the group, so the file is watched too.
        var fileChanges = groupChanges
            .Select(_ => FileOf(connection.Db, id))
            .DistinctUntilChanged()
            .Select(file => file is { } known ? connection.ObserveDatoms(known).QueryWhenChanged(static _ => 0).Prepend(0) : Observable.Return(0))
            .Switch();

        return listings
            .CombineLatest(groupChanges, fileChanges, (listing, _, _) => listing is null ? null : NewerRelease(connection.Db, id, listing))
            .DistinctUntilChanged(static offer => offer is null ? null : $"{offer.Value.Installed}|{offer.Value.Entry.Repository}|{offer.Value.Entry.MachineUrl}|{offer.Value.Entry.Version}|{offer.Value.Entry.DownloadHash}")
            .Select(ICollectionUpdate? (offer) => offer is { } known
                ? new WabbajackCollectionUpdate(services, WabbajackModlistLoadoutGroup.Load(connection.Db, id), known.Entry)
                : null);
    }

    private static EntityId? FileOf(IDb db, EntityId group)
    {
        var current = WabbajackModlistLoadoutGroup.Load(db, group);
        return current.IsValid() ? current.ModlistId.Value : null;
    }

    private readonly record struct Offer(EntityId Installed, GalleryEntry Entry);

    private static Offer? NewerRelease(IDb db, EntityId id, GalleryListing listing)
    {
        var group = WabbajackModlistLoadoutGroup.Load(db, id);
        if (!group.IsValid()) return null;

        // An update that stopped halfway is finished or undone first; Apply refuses another one, after the whole download.
        if (ModlistInstallRuns.IsMidUpdate(group)) return null;

        var installed = group.Modlist;
        if (!installed.IsValid()) return null;
        return ModlistIdentity.EntryFor(listing, installed) is { } entry && ModlistIdentity.IsNewer(entry, installed) ? new Offer(installed.Id, entry) : null;
    }

    public IObservable<IChangeSet<CollectionSource, EntityId>> ObservePending(Loadout.ReadOnly loadout)
    {
        var connection = services.GetRequiredService<IConnection>();
        var loadoutId = loadout.LoadoutId;
        // The exact title: a Skyrim VR list is not offered to a Skyrim SE loadout, whatever domain Nexus files them under.
        var games = new LoadoutAcquisition(loadoutId).Destination(connection.Db).Games;

        return WabbajackModlistLibraryFile
            .ObserveAll(connection)
            .FilterImmutable(file => WabbajackGames.GameIdFor(file.GameType) is { } game && games.Contains(game))
            .FilterOnObservable(file => IsPending(connection, file.Id, loadoutId))
            .Transform(CollectionSource (file) => new WabbajackModlistSource(services, loadoutId, file));
    }

    /// <summary>
    /// Whether the file is offered as an install: not installed in the loadout, and not a version of a list the loadout
    /// already has. The version an update switched away from, and the release an update would switch to, are not
    /// installs; a list whose update stopped halfway is its group's current list, so its entry to resume stays.
    /// </summary>
    private static IObservable<bool> IsPending(IConnection connection, EntityId file, LoadoutId loadout)
    {
        static bool Read(IDb db, EntityId file, LoadoutId loadout)
        {
            var list = WabbajackModlistLibraryFile.Load(db, file);
            if (!list.IsValid()) return false;
            return WabbajackModlistInstallState.Read(db, LibraryFileId.From(file), loadout) is WabbajackModlistCardState.NotInstalled or WabbajackModlistCardState.Incomplete
                && !ModlistUpdater.IsSupersededOrPendingUpdate(db, list, loadout);
        }

        // Deferred so the first value is the state when someone subscribes, as WabbajackModlistInstallState.Observe does.
        return Observable.Defer(() => connection.Revisions
            .Select(db => Read(db, file, loadout))
            .Prepend(Read(connection.Db, file, loadout))
            .DistinctUntilChanged());
    }

    private static WabbajackModlistLoadoutGroup.ReadOnly ModlistGroup(CollectionGroup.ReadOnly group) =>
        group.TryGetAsWabbajackModlistLoadoutGroup(out var modlist)
            ? modlist
            : throw new InvalidOperationException($"Collection group {group.Id} is not a Wabbajack modlist");
}

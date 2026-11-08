using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.App.UI.Pages.Collections.Update;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Helpers;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// Nexus Mods collections: a group is a <see cref="NexusCollectionLoadoutGroup"/>, and every revision of the loadout's
/// game that is in the library and not installed there is pending.
/// </summary>
public sealed class NexusCollectionProvider(IServiceProvider services) : ICollectionProvider
{
    public bool Owns(CollectionGroup.ReadOnly group) => group.TryGetAsNexusCollectionLoadoutGroup(out _);

    public CollectionSource ForGroup(CollectionGroup.ReadOnly group, LoadoutId loadout) =>
        NexusCollectionSource.Installed(services, loadout, NexusGroup(group));

    public IObservable<bool> IsListedAsInstalled(CollectionGroup.ReadOnly group) =>
        services.GetRequiredService<CollectionDownloader>().IsListedAsInstalledObservable(NexusGroup(group));

    /// <summary>
    /// An update is offered while the latest published revision recorded on the group (<see cref="ManagedCollectionLoadoutGroup.LastPublishedRevisionNumber"/>,
    /// written when the collection's page learns it) is newer than the revision the group is on. Only a change of that
    /// pair emits again, so the menu isn't rebuilt per datom.
    /// </summary>
    public IObservable<ICollectionUpdate?> ObserveUpdate(CollectionGroup.ReadOnly group)
    {
        var id = NexusGroup(group).Id;
        var connection = services.GetRequiredService<IConnection>();

        return connection.ObserveDatoms(id)
            .QueryWhenChanged(_ => NewerRevision(connection.Db, id))
            .Prepend(NewerRevision(connection.Db, id))
            .DistinctUntilChanged()
            .Select(ICollectionUpdate? (offer) => offer is { } known
                ? new NexusCollectionUpdate(services, NexusCollectionLoadoutGroup.Load(connection.Db, id), known.Target)
                : null);
    }

    /// <summary>The revision the group is on and the newer one published, which the offer moves it to.</summary>
    private readonly record struct Offer(RevisionNumber Installed, RevisionNumber Target);

    private static Offer? NewerRevision(IDb db, EntityId id)
    {
        var installed = NexusCollectionLoadoutGroup.Load(db, id);
        if (!installed.IsValid()) return null;
        if (!ManagedCollectionLoadoutGroup.LastPublishedRevisionNumber.TryGetValue(installed, out var published)) return null;
        var current = installed.Revision.RevisionNumber;
        return published > current ? new Offer(current, published) : null;
    }

    public IObservable<IChangeSet<CollectionSource, EntityId>> ObservePending(Loadout.ReadOnly loadout)
    {
        var game = loadout.InstallationInstance.Game;
        var downloader = services.GetRequiredService<CollectionDownloader>();
        var loadoutId = loadout.LoadoutId;

        return CollectionRevisionMetadata
            .ObserveAll(services.GetRequiredService<IConnection>())
            .FilterImmutable(revision => game.NexusModsGameId.IsNexusGame(revision.Collection.GameId))
            .FilterOnObservable(revision => downloader.IsOfferedForInstallObservable(revision, loadoutId))
            .Transform(CollectionSource (revision) => NexusCollectionSource.Pending(services, loadoutId, revision));
    }

    private static NexusCollectionLoadoutGroup.ReadOnly NexusGroup(CollectionGroup.ReadOnly group) =>
        group.TryGetAsNexusCollectionLoadoutGroup(out var nexus)
            ? nexus
            : throw new InvalidOperationException($"Collection group {group.Id} is not a Nexus collection");
}

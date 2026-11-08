using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.App.UI.Pages.Collections.Update;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.App.UI.Helpers;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
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

    public IObservable<ICollectionUpdate?> ObserveUpdate(CollectionGroup.ReadOnly group) => Observable.Return<ICollectionUpdate?>(null);

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

using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.App.UI.Pages.Collections.Update;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// The fallback for a group no other provider owns: a collection the user made. It is always listed and never pending.
/// </summary>
public sealed class LocalCollectionProvider(IServiceProvider services) : ICollectionProvider
{
    public bool Owns(CollectionGroup.ReadOnly group) => true;

    public CollectionSource ForGroup(CollectionGroup.ReadOnly group, LoadoutId loadout) => new LocalCollectionSource(services, loadout, group);

    public IObservable<bool> IsListedAsInstalled(CollectionGroup.ReadOnly group) => Observable.Return(true);

    public IObservable<ICollectionUpdate?> ObserveUpdate(CollectionGroup.ReadOnly group) => Observable.Return<ICollectionUpdate?>(null);

    public IObservable<IChangeSet<CollectionSource, EntityId>> ObservePending(Loadout.ReadOnly loadout) =>
        Observable.Return(ChangeSet<CollectionSource, EntityId>.Empty);
}

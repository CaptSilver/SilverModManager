using SilverModManager.Abstractions.Collections;
using DynamicData;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// The registered providers plus the local fallback. The only thing the left menu, the page factory and the mod row's
/// "open collection" handler hold.
/// </summary>
public sealed class CollectionProviders(IEnumerable<ICollectionProvider> providers, LocalCollectionProvider fallback)
{
    private readonly ICollectionProvider[] _providers = providers.ToArray();

    private ICollectionProvider ProviderOf(CollectionGroup.ReadOnly group) =>
        _providers.FirstOrDefault(provider => provider.Owns(group)) ?? fallback;

    public CollectionSource For(CollectionGroup.ReadOnly group, LoadoutId loadout) => ProviderOf(group).ForGroup(group, loadout);

    public IObservable<bool> IsListedAsInstalled(CollectionGroup.ReadOnly group) => ProviderOf(group).IsListedAsInstalled(group);

    /// <summary>
    /// Every provider's pending collections as one changeset. Keys are revision ids for Nexus and library file ids for
    /// Wabbajack, so they never collide.
    /// </summary>
    public IObservable<IChangeSet<CollectionSource, EntityId>> ObservePending(Loadout.ReadOnly loadout) =>
        _providers.Select(provider => provider.ObservePending(loadout)).MergeChangeSets();
}

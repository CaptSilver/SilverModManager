using SilverModManager.Abstractions.Collections;
using SilverModManager.App.UI.Pages.Collections.Update;
using DynamicData;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// The one thing that knows how a kind of collection is found: which source backs a group, whether the menu lists it
/// as installed, and which collections of this kind could be installed into a loadout but are not.
/// </summary>
public interface ICollectionProvider
{
    /// <summary>Whether this provider's source backs the group. The local provider is the fallback and is not asked.</summary>
    bool Owns(CollectionGroup.ReadOnly group);

    CollectionSource ForGroup(CollectionGroup.ReadOnly group, LoadoutId loadout);

    /// <summary>
    /// Whether the menu lists the group as installed (the toggle entry). Nexus: every required mod is installed;
    /// Wabbajack: the install finished; local: always.
    /// </summary>
    IObservable<bool> IsListedAsInstalled(CollectionGroup.ReadOnly group);

    /// <summary>
    /// The newer version of the installed group, when one is known. A source with nothing to offer answers
    /// <c>Observable.Return&lt;ICollectionUpdate?&gt;(null)</c>: "no update known" is a real value, and the page adds
    /// its Update item only for a non-null one.
    /// </summary>
    IObservable<ICollectionUpdate?> ObserveUpdate(CollectionGroup.ReadOnly group);

    /// <summary>
    /// Collections of this kind that belong to the loadout's game and are not installed in it: the pending entries.
    /// Keyed by the entity that identifies the collection (revision metadata, modlist library file).
    /// </summary>
    IObservable<IChangeSet<CollectionSource, EntityId>> ObservePending(Loadout.ReadOnly loadout);
}

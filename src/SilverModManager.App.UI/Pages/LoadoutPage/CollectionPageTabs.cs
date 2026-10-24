using DynamicData.Kernel;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

public enum CollectionPageTab
{
    /// <summary>The collection's mods in the loadout.</summary>
    Installed,

    /// <summary>The Required, Optional and instructions tabs of the collection's download page.</summary>
    Downloads,
}

public static class CollectionPageTabs
{
    /// <summary>
    /// The tabs a collection's page offers. A local collection has no Nexus revision to download from,
    /// and a Nexus one whose archive left the library has nothing to list.
    /// </summary>
    public static IReadOnlyList<CollectionPageTab> For(bool hasDownloadPage)
    {
        return hasDownloadPage
            ? [CollectionPageTab.Installed, CollectionPageTab.Downloads]
            : [CollectionPageTab.Installed];
    }

    /// <summary>
    /// The collection's page on Nexus Mods, at the revision that was installed.
    /// </summary>
    public static Uri NexusModsUri(GameDomain gameDomain, CollectionSlug slug, RevisionNumber revision)
    {
        return NexusModsUrlBuilder.GetCollectionUri(gameDomain, slug, Optional<RevisionNumber>.Create(revision));
    }
}

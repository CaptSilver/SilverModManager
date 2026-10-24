using DynamicData.Kernel;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

public enum CollectionPageTab
{
    /// <summary>The collection's mods in the loadout.</summary>
    Installed,

    /// <summary>The collection author's instructions for the whole collection and for single mods.</summary>
    Instructions,

    /// <summary>The Required, Optional and instructions tabs of the collection's download page.</summary>
    Downloads,
}

public static class CollectionPageTabs
{
    /// <summary>
    /// The tabs a collection's page offers. A local collection has no Nexus revision to download from,
    /// and a Nexus one whose archive left the library has nothing to list.
    /// The instructions tab appears only when the author wrote some.
    /// </summary>
    public static IReadOnlyList<CollectionPageTab> For(bool hasDownloadPage, bool hasInstructions = false)
    {
        var tabs = new List<CollectionPageTab> { CollectionPageTab.Installed };
        if (hasInstructions) tabs.Add(CollectionPageTab.Instructions);
        if (hasDownloadPage) tabs.Add(CollectionPageTab.Downloads);
        return tabs;
    }

    /// <summary>
    /// The collection's page on Nexus Mods, at the revision that was installed.
    /// </summary>
    public static Uri NexusModsUri(GameDomain gameDomain, CollectionSlug slug, RevisionNumber revision)
    {
        return NexusModsUrlBuilder.GetCollectionUri(gameDomain, slug, Optional<RevisionNumber>.Create(revision));
    }
}

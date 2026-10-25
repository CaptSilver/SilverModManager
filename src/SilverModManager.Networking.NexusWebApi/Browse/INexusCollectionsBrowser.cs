namespace SilverModManager.Networking.NexusWebApi;

/// <summary>What the Nexus Mods browse page needs from the site to list a game's collections.</summary>
public interface INexusCollectionsBrowser
{
    /// <summary>Fetches one page of the game's collections. Throws when the API answers without data.</summary>
    Task<NexusCollectionsSearchPage> Search(NexusCollectionsSearchQuery query, CancellationToken cancellationToken);
}

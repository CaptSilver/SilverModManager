using DynamicData.Kernel;
using JetBrains.Annotations;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Abstractions.NexusWebApi;

/// Builds the URLs that point at Nexus Mods. Anything that exposes a Nexus Mods
/// URL to the user should go through this class so the URL layout lives in one place.
[PublicAPI]
public static class NexusModsUrlBuilder
{
    private const string BaseUrl = "https://www.nexusmods.com";
    private const string UsersBaseUrl = "https://users.nexusmods.com";

    /// <summary>
    /// Uri for the user settings page.
    /// </summary>
    /// <example>
    /// <c>https://users.nexusmods.com</c>
    /// </example>
    public static readonly Uri UserSettingsUri = new Uri(UsersBaseUrl);

    /// <summary>
    /// Returns a URI for a user profile.
    /// </summary>
    public static Uri GetProfileUri(UserId userId)
    {
        // https://www.nexusmods.com/users/6672467
        var url = $"{BaseUrl}/users/{userId}";
        return new Uri(url);
    }

    /// <summary>
    /// Returns a URI for a game page.
    /// </summary>
    public static Uri GetGameUri(GameDomain gameDomain)
    {
        // https://www.nexusmods.com/games/stardewvalley
        var url = $"{BaseUrl}/games/{gameDomain}";
        return new Uri(url);
    }

    /// <summary>
    /// Returns a URI for a mod page.
    /// </summary>
    public static Uri GetModUri(GameDomain gameDomain, ModId modId)
    {
        // https://www.nexusmods.com/stardewvalley/mods/2400
        var url = $"{BaseUrl}/{gameDomain}/mods/{modId}";
        return new Uri(url);
    }

    /// <summary>
    /// Returns a URI for a file download page.
    /// </summary>
    /// <remarks>
    /// <paramref name="useNxmLink"/> changes how the download button on the page behaves. If set to
    /// <c>true</c>, the download button will open an NXM link, if set to <c>false</c> the download
    /// will happen in the browser.
    /// </remarks>
    public static Uri GetFileDownloadUri(GameDomain gameDomain, ModId modId, FileId fileId, bool useNxmLink)
    {
        // https://www.nexusmods.com/stardewvalley/mods/2400?tab=files&file_id=128328&nmm=0
        // https://www.nexusmods.com/stardewvalley/mods/2400?tab=files&file_id=128328&nmm=1
        var url = $"{BaseUrl}/{gameDomain}/mods/{modId}?tab=files&file_id={fileId}&nmm={Convert.ToInt32(useNxmLink)}";
        return new Uri(url);
    }

    /// <summary>
    /// Returns a URI for a game's browse collections page.
    /// </summary>
    public static Uri GetBrowseCollectionsUri(GameDomain gameDomain)
    {
        // https://www.nexusmods.com/games/stardewvalley/collections
        // https://www.nexusmods.com/games/cyberpunk2077/collections
        var url = $"{BaseUrl}/games/{gameDomain}/collections";
        return new Uri(url);
    }
    
    /// <summary>
    /// Returns a URI for a collection page.
    /// </summary>
    public static Uri GetCollectionUri(GameDomain gameDomain, CollectionSlug collectionSlug, Optional<RevisionNumber> revisionNumber)
    {
        // https://www.nexusmods.com/games/stardewvalley/collections/tckf0m
        // https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/revisions/80
        var url = $"{BaseUrl}/games/{gameDomain}/collections/{collectionSlug}{(revisionNumber.HasValue ? $"/revisions/{revisionNumber.Value}" : string.Empty)}";
        return new Uri(url);
    }

    /// <summary>
    /// Returns a URI for the bugs page of a collection.
    /// </summary>
    public static Uri GetCollectionBugsUri(GameDomain gameDomain, CollectionSlug collectionSlug, Optional<RevisionNumber> revisionNumber)
    {
        // https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/revisions/80/bugs
        var url = $"{BaseUrl}/games/{gameDomain}/collections/{collectionSlug}{(revisionNumber.HasValue ? $"/revisions/{revisionNumber.Value}" : string.Empty)}/bugs";
        return new Uri(url);
    }
    
    /// <summary>
    /// Returns a URI for the changelog page of a collection.
    /// </summary>
    public static Uri GetCollectionChangelogUri(GameDomain gameDomain, CollectionSlug collectionSlug, Optional<RevisionNumber> revisionNumber)
    {
        // https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/revisions/80/changelog
        var url = $"{BaseUrl}/games/{gameDomain}/collections/{collectionSlug}{(revisionNumber.HasValue ? $"/revisions/{revisionNumber.Value}" : string.Empty)}/changelog";
        return new Uri(url);
    }

    /// <summary>
    /// Uri for the premium benefits page.
    /// </summary>
    /// <example>
    /// <c>https://www.nexusmods.com/premium</c>
    /// </example>
    public static readonly Uri LearnAboutPremiumUri = new Uri($"{BaseUrl}/premium");

    /// <summary>
    /// Uri for the upgrade to premium page.
    /// </summary>
    /// <example>
    /// <c>https://users.nexusmods.com/account/billing/premium</c>
    /// </example>
    public static readonly Uri UpgradeToPremiumUri = new Uri($"{UsersBaseUrl}/account/billing/premium");
}

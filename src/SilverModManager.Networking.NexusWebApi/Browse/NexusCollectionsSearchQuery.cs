using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>
/// How the Nexus Mods browse page orders a game's collections. There is no name order: the collections search has no
/// name sort, and sorting the loaded pages alone would make the order jump as more of them arrive.
/// </summary>
public enum NexusCollectionsSort
{
    Endorsements,
    Downloads,
    Updated,
}

/// <summary>
/// One page of a game's collections: those whose name contains <paramref name="SearchText"/> (all of them when it is
/// blank), starting at <paramref name="Offset"/>. Adult collections are left out unless <paramref name="IncludeAdult"/>.
/// </summary>
public sealed record NexusCollectionsSearchQuery(
    NexusModsGameId Game,
    string SearchText,
    NexusCollectionsSort Sort,
    int Offset,
    int Count,
    bool IncludeAdult = false);

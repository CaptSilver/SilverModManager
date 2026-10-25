using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>How the Nexus Mods browse page orders a game's mods.</summary>
public enum NexusModsSort
{
    Endorsements,
    Downloads,
    Updated,
    Name,
}

/// <summary>
/// One page of a game's mod catalog: the mods whose name contains <paramref name="SearchText"/> (all of them when it
/// is blank), starting at <paramref name="Offset"/>. Adult mods are left out unless <paramref name="IncludeAdult"/>.
/// </summary>
public sealed record NexusModsSearchQuery(
    NexusModsGameId Game,
    string SearchText,
    NexusModsSort Sort,
    int Offset,
    int Count,
    bool IncludeAdult = false);

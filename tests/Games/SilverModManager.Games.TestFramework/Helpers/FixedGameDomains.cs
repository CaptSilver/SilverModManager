using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.TestFramework.Helpers;

/// <summary>Maps one game domain to one Nexus game id without asking the API.</summary>
public sealed class FixedGameDomains(GameDomain domain, NexusModsGameId id) : IGameDomainToGameIdMappingCache
{
    public GameDomain GetDomain(NexusModsGameId _) => domain;
    public NexusModsGameId GetId(GameDomain _) => id;
}

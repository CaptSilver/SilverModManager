using System.Text.Json.Serialization;
using SilverModManager.Abstractions.GOG.DTOs;

namespace SilverModManager.Networking.GOG.DTOs;

internal class DepotResponse
{
    [JsonPropertyName("depot")]
    public required DepotInfo Depot { get; init; }
        
    [JsonPropertyName("version")]
    public required int Version { get; init; }
}

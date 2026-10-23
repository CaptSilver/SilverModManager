using System.Text.Json.Serialization;
using SilverModManager.Abstractions.GOG.DTOs;

namespace SilverModManager.Networking.GOG.DTOs;

internal class BuildListResponse
{
    [JsonPropertyName("items")]
    public required Build[] Items { get; init; }
}

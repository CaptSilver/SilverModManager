using System.Text.Json.Serialization;
using SilverModManager.Abstractions.GOG.DTOs;
using SilverModManager.Abstractions.GOG.Values;

namespace SilverModManager.Networking.GOG.DTOs;

internal class SecureUrlResponse
{
    /// <summary>
    /// The product ID for the secure URLs.
    /// </summary>
    [JsonPropertyName("product_id")]
    public required ProductId ProductId { get; init; }
        
    /// <summary>
    /// The type of URLs in this response, normally this is `depot` but could be `patch` or something similar
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }
        
    /// <summary>
    /// The URLs for the product.
    /// </summary>
    [JsonPropertyName("urls")]
    public required SecureUrl[] Urls { get; init; }
}

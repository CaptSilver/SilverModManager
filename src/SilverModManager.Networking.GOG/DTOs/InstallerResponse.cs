using System.Text.Json.Serialization;

namespace SilverModManager.Networking.GOG.DTOs;

public record InstallerResponse(
    [property: JsonPropertyName("downlink")] string DownloadLink,
    [property: JsonPropertyName("checksum")] string ChecksumLink
);

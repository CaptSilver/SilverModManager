using System.Text.Json.Serialization;
using JetBrains.Annotations;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Abstractions.Collections.Json;

/// <summary>
/// One mod in the app's own load order key. Any of md5, ids and name may be absent; a reader matches
/// them in that order against what is installed.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SilverLoadOrderEntry
{
    [JsonPropertyName("md5")]
    public Md5Value Md5 { get; init; }

    [JsonPropertyName("modId")]
    public ModId? ModId { get; init; }

    [JsonPropertyName("fileId")]
    public FileId? FileId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

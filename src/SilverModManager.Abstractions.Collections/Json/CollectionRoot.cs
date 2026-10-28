using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace SilverModManager.Abstractions.Collections.Json;

/// <summary>
/// DTO representing the `collection.json` file
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class CollectionRoot
{
    [JsonPropertyName("info")]
    public required CollectionInfo Info { get; init; }
    
    [JsonPropertyName("mods")]
    public Mod[] Mods { get; init; } = [];

    /// <summary>
    /// Vortex-styled rules for "mods".
    /// </summary>
    [JsonPropertyName("modRules")]
    public ModRule[] ModRules { get; init; } = [];

    /// <summary>
    /// Vortex's order key for Witcher 3, kept raw: its shape belongs to the game, and it is written back unchanged.
    /// </summary>
    [JsonPropertyName("loadOrder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? LoadOrder { get; init; }

    /// <summary>
    /// Vortex's plugin order key for Gamebryo games, kept raw.
    /// </summary>
    [JsonPropertyName("plugins")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Plugins { get; init; }

    /// <summary>
    /// Vortex's LOOT rules for the collection's plugins (<c>{ plugins: [...], groups: [...] }</c>), kept raw. Not an order key.
    /// </summary>
    [JsonPropertyName("pluginRules")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? PluginRules { get; init; }

    /// <summary>
    /// Vortex's Witcher 3 extension destructures this object on import before it reads <c>loadOrder</c>, so a Witcher 3
    /// collection without it fails to import. Kept raw.
    /// </summary>
    [JsonPropertyName("mergedData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? MergedData { get; init; }

    /// <summary>
    /// The app's own mod order, winner first. Vortex ignores keys it does not know, so this does not disturb it.
    /// </summary>
    [JsonPropertyName("silverLoadOrder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SilverLoadOrderEntry[]? SilverLoadOrder { get; init; }
}

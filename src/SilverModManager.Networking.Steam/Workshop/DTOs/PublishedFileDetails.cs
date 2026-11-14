using System.Text.Json;
using System.Text.Json.Serialization;

namespace SilverModManager.Networking.Steam.Workshop.DTOs;

/// <summary>
/// What Steam says about one Workshop item. An item that was removed or never existed comes back with a
/// <see cref="Result"/> other than 1 and nothing else.
/// </summary>
/// <param name="Id">The published file id, the number in the item's page address and the name of its folder.</param>
/// <param name="Result">Steam's result code: 1 for an item, 9 for one that does not exist or was removed.</param>
/// <param name="TimeUpdated">When the author last updated the item, which is when Steam next downloads it.</param>
/// <param name="FileSize">The size of the item's content in bytes, as Steam spells it.</param>
/// <param name="PreviewUrl">The item's preview image.</param>
/// <param name="ConsumerAppId">The game the item is for.</param>
/// <param name="Visibility">0 public, 1 friends only, 2 private, 3 unlisted.</param>
/// <param name="Banned">1 when Steam took the item down.</param>
public sealed record PublishedFileDetails(
    [property: JsonPropertyName("publishedfileid")] ulong Id,
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("time_updated"), JsonConverter(typeof(UnixSecondsConverter))] DateTimeOffset? TimeUpdated = null,
    [property: JsonPropertyName("file_size")] long? FileSize = null,
    [property: JsonPropertyName("preview_url")] Uri? PreviewUrl = null,
    [property: JsonPropertyName("consumer_app_id")] uint? ConsumerAppId = null,
    [property: JsonPropertyName("visibility")] int? Visibility = null,
    [property: JsonPropertyName("banned")] int? Banned = null)
{
    /// <summary>Whether Steam returned the item itself rather than a result code for a missing one.</summary>
    [JsonIgnore]
    public bool IsAvailable => Result == 1 && Banned != 1;
}

internal sealed class UnixSecondsConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } time) writer.WriteNumberValue(time.ToUnixTimeSeconds());
        else writer.WriteNullValue();
    }
}

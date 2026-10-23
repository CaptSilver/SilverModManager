using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexusMods.Games.CreationEngine.Creations;

/// <summary>One entry of <c>ContentCatalog.txt</c>.</summary>
/// <param name="Key">The entry's key (<c>CSV2_n</c> for Skyrim, <c>TM_&lt;guid&gt;</c> for Starfield).</param>
/// <param name="Title">The Creation's title.</param>
/// <param name="Version">Only when it is digits and dots; the catalog also carries junk such as
/// <c>150669.== Version Number ==</c>.</param>
/// <param name="Files">Bare file names relative to the Data folder, not paths from the game root.</param>
public sealed record ContentCatalogEntry(string Key, string Title, string? Version, IReadOnlyList<string> Files);

/// <summary>
/// Reads the <c>ContentCatalog.txt</c> the Creation Engine games keep next to their plugin list: a JSON
/// object, UTF-8 with or without a BOM, whose first entry is a <c>ContentCatalog</c> header and whose other
/// entries are the installed Creations.
/// </summary>
public static partial class ContentCatalogReader
{
    private const string HeaderKey = "ContentCatalog";

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)*$")]
    private static partial Regex DottedVersion();

    /// <exception cref="CreationsRecordException">The stream is not a catalog.</exception>
    public static IReadOnlyList<ContentCatalogEntry> Read(Stream stream)
    {
        try
        {
            // JsonDocument accepts a leading UTF-8 BOM.
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new CreationsRecordException("The content catalog is not a JSON object");

            var entries = new List<ContentCatalogEntry>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == HeaderKey) continue;
                entries.Add(ReadEntry(property));
            }
            return entries;
        }
        catch (JsonException e)
        {
            throw new CreationsRecordException("The content catalog is not valid JSON", e);
        }
    }

    private static ContentCatalogEntry ReadEntry(JsonProperty property)
    {
        var value = property.Value;
        if (value.ValueKind != JsonValueKind.Object)
            throw new CreationsRecordException($"The content catalog entry '{property.Name}' is not an object");

        var title = value.TryGetProperty("Title", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { } named
            ? named
            : property.Name;

        string? version = null;
        if (value.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString() is { } text && DottedVersion().IsMatch(text))
            version = text;

        var files = new List<string>();
        if (value.TryGetProperty("Files", out var f) && f.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in f.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.String || file.GetString() is not { } name)
                    throw new CreationsRecordException($"The content catalog entry '{property.Name}' lists a file that is not a string");
                files.Add(name);
            }
        }

        return new ContentCatalogEntry(property.Name, title, version, files);
    }
}

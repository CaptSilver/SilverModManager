using System.Text.Json;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Gallery;

/// <summary>
/// Parses the gallery's JSON. The documents come from third-party repositories, so a field of the wrong type is
/// dropped, an entry that can't be a card is skipped, and only a document of the wrong overall shape is an error.
/// </summary>
public static class GalleryReader
{
    public sealed record Repository(string Name, Uri Url);

    /// <summary>
    /// <c>repositories.json</c>: an object from repository name to the address of its <c>modlists.json</c>. Addresses
    /// that aren't https are left out.
    /// </summary>
    public static IReadOnlyList<Repository> ReadRepositories(string json)
    {
        using var document = Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new GalleryFormatException("repositories.json is not a JSON object");

        var result = new List<Repository>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            if (HttpsUri(property.Value.GetString()) is not { } url) continue;
            result.Add(new Repository(property.Name, url));
        }
        return result;
    }

    /// <summary>A repository's <c>modlists.json</c>: an array of entries.</summary>
    public static IReadOnlyList<GalleryEntry> ReadModlists(string json, string repository)
    {
        using var document = Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new GalleryFormatException("modlists.json is not a JSON array");

        var entries = new List<GalleryEntry>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (ReadEntry(element, repository) is { } entry) entries.Add(entry);
        }
        return entries;
    }

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new GalleryFormatException($"not valid JSON: {e.Message}");
        }
    }

    private static GalleryEntry? ReadEntry(JsonElement element, string repository)
    {
        var title = String(element, "title");
        var game = String(element, "game");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(game)) return null;

        var links = Object(element, "links");
        var metadata = Object(element, "download_metadata");
        var machineUrl = links is { } l ? String(l, "machineURL") : null;
        // The id is how a list is told apart from the others; without one fall back to something that is still unique per repository and title.
        machineUrl = string.IsNullOrWhiteSpace(machineUrl) ? $"{repository}/{title}" : machineUrl;

        return new GalleryEntry
        {
            Title = title,
            Description = String(element, "description") ?? "",
            Author = String(element, "author") ?? "",
            Maintainers = Strings(element, "maintainers"),
            Tags = Strings(element, "tags"),
            Version = String(element, "version") ?? "",
            GalleryGame = game,
            MachineUrl = machineUrl,
            Repository = repository,
            IsOfficial = Bool(element, "official"),
            IsNsfw = Bool(element, "nsfw"),
            IsUtility = Bool(element, "utility_list"),
            IsForcedDown = Bool(element, "force_down"),
            Image = links is { } a ? HttpsUri(String(a, "image")) : null,
            Readme = links is { } b ? HttpsUri(String(b, "readme")) : null,
            Download = links is { } c ? HttpsUri(String(c, "download")) : null,
            Website = links is { } w ? HttpsUri(String(w, "websiteURL")) : null,
            DateCreated = Date(element, "dateCreated"),
            DateUpdated = Date(element, "dateUpdated"),
            DownloadHash = metadata is { } m ? ParseHash(String(m, "Hash")) : null,
            DownloadSize = metadata is { } m1 ? ULong(m1, "Size") : null,
            ArchiveCount = metadata is { } m2 ? Int(m2, "NumberOfArchives") : null,
            ArchivesSize = metadata is { } m3 ? ULong(m3, "SizeOfArchives") : null,
            InstalledFileCount = metadata is { } m4 ? Int(m4, "NumberOfInstalledFiles") : null,
            InstalledSize = metadata is { } m5 ? ULong(m5, "SizeOfInstalledFiles") : null,
        };
    }

    // Wabbajack's serializer ignores case, and the gallery has used both snake_case and camelCase for the flags.
    private static string Normalise(string name) => name.Replace("_", "").ToLowerInvariant();

    private static JsonElement? Find(JsonElement parent, string name)
    {
        var wanted = Normalise(name);
        foreach (var property in parent.EnumerateObject())
        {
            if (Normalise(property.Name) != wanted) continue;
            return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
        }
        return null;
    }

    private static JsonElement? Object(JsonElement parent, string name)
        => Find(parent, name) is { ValueKind: JsonValueKind.Object } value ? value : null;

    private static string? String(JsonElement parent, string name)
        => Find(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static DateTimeOffset? Date(JsonElement parent, string name)
        => DateTimeOffset.TryParse(String(parent, name), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    private static bool Bool(JsonElement parent, string name)
        => Find(parent, name) is { ValueKind: JsonValueKind.True };

    private static ulong? ULong(JsonElement parent, string name)
        => Find(parent, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetUInt64(out var number) ? number : null;

    private static int? Int(JsonElement parent, string name)
        => Find(parent, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) && number >= 0 ? number : null;

    private static IReadOnlyList<string> Strings(JsonElement parent, string name)
    {
        if (Find(parent, name) is not { ValueKind: JsonValueKind.Array } array) return [];
        return array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .OfType<string>()
            .Where(s => s.Length > 0)
            .ToArray();
    }

    // Links are only ever followed over https; the gallery is not trusted to point the app at file: or other schemes.
    private static Uri? HttpsUri(string? text)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    private static Hash? ParseHash(string? base64)
    {
        if (base64 is null) return null;
        try { return WabbajackHash.Parse(base64); }
        catch (FormatException) { return null; }
    }
}

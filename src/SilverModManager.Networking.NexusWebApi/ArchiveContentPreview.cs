using System.Text.Json;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>A file inside an archive uploaded to Nexus Mods.</summary>
/// <param name="Path">The path inside the archive, with <c>/</c> separators.</param>
public sealed record ArchiveContentEntry(string Path, long Size);

/// <summary>
/// The file listing Nexus Mods keeps for each uploaded archive (the <c>content_preview_link</c> of a file): a tree of
/// <c>children</c> with a <c>name</c>, a <c>type</c> and, for files, a <c>size</c> that arrives as text or a number.
/// </summary>
public static class ArchiveContentPreview
{
    private const int MaxDepth = 64;
    private const int MaxEntries = 100_000;

    /// <summary>Every file of the listing, depth first, in the listing's order. Throws <see cref="FormatException"/> when the text is not a listing.</summary>
    public static IReadOnlyList<ArchiveContentEntry> Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth + 8 });
        }
        catch (JsonException e)
        {
            throw new FormatException("The archive listing is not JSON.", e);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("The archive listing is not a JSON object.");

            var entries = new List<ArchiveContentEntry>();
            Walk(document.RootElement, "", 0, entries);
            return entries;
        }
    }

    // Only the root may be anonymous; below it the path is built from the names, not from the listing's own path field.
    private static void Walk(JsonElement node, string prefix, int depth, List<ArchiveContentEntry> entries)
    {
        if (depth > MaxDepth || entries.Count >= MaxEntries) return;
        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return;

        foreach (var child in children.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.Object || !child.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String) continue;

            var name = nameElement.GetString() ?? "";
            var path = prefix.Length == 0 ? name : $"{prefix}/{name}";
            if (child.TryGetProperty("children", out _))
            {
                Walk(child, path, depth + 1, entries);
            }
            else if (SizeOf(child) is { } size)
            {
                entries.Add(new ArchiveContentEntry(path, size));
            }
        }
    }

    private static long? SizeOf(JsonElement node)
    {
        if (!node.TryGetProperty("size", out var size)) return null;

        return size.ValueKind switch
        {
            JsonValueKind.Number when size.TryGetInt64(out var number) && number >= 0 => number,
            JsonValueKind.String when long.TryParse(size.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>Whether <paramref name="link"/> is an https address on nexusmods.com, the only place a listing is fetched from.</summary>
    public static bool IsTrustedLink(string? link)
        => Uri.TryCreate(link, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && (uri.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase));

    public static async Task<IReadOnlyList<ArchiveContentEntry>> FetchAsync(HttpClient client, string link, CancellationToken cancellationToken)
    {
        if (!IsTrustedLink(link)) throw new InvalidOperationException($"'{link}' is not an https link on nexusmods.com, so the archive listing is not fetched from it.");

        using var response = await client.GetAsync(link, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}

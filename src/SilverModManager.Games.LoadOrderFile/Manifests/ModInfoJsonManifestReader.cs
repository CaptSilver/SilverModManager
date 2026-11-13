using System.Text;
using System.Text.Json;
using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile.Manifests;

/// <summary>
/// Starsector's <c>mod_info.json</c>. The game reads it as relaxed JSON (comments, trailing commas), and lists a mod by
/// its <c>id</c> exactly as written. The file has no order lists. Its <c>version</c> is a string in some mods and an
/// object in others, so it is not read.
/// </summary>
public sealed class ModInfoJsonManifestReader : IModManifestReader
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public RelativePath ManifestPath => "mod_info.json";

    public ModManifest? Read(byte[] bytes)
    {
        var text = bytes.AsSpan();
        if (text.StartsWith(Encoding.UTF8.GetPreamble())) text = text[Encoding.UTF8.GetPreamble().Length..];

        try
        {
            using var document = JsonDocument.Parse(text.ToArray(), Options);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            var root = document.RootElement;
            var id = StringOf(root, "id")?.Trim();
            if (string.IsNullOrEmpty(id)) return null;

            return new ModManifest(id, StringOf(root, "name"), Dependencies(root), [], [], [], [], []);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> Dependencies(JsonElement root)
    {
        if (!root.TryGetProperty("dependencies", out var list) || list.ValueKind != JsonValueKind.Array) return [];

        return list.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => StringOf(item, "id"))
            .OfType<string>()
            .ToList();
    }
}

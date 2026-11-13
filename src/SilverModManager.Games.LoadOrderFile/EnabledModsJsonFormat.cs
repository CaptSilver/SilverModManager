using System.Text.Json;
using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// Starsector's <c>mods/enabled_mods.json</c>: an <c>enabledMods</c> array of mod ids. Whether the game loads the mods
/// in the array's order is not confirmed, so a render sorts the ids and the file stays the same between applies when
/// nothing else changed.
/// </summary>
public sealed class EnabledModsJsonFormat : ILoadOrderFileFormat
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, NewLine = "\n" };

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public string? ModOf(RelativePath fileInModFolder)
    {
        var parts = fileInModFolder.Path.Split('/');
        return parts.Length >= 2 ? parts[0] : null;
    }

    public IReadOnlyList<string> Listed(string fileText)
    {
        try
        {
            using var document = JsonDocument.Parse(fileText.TrimStart('﻿'), ReadOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!document.RootElement.TryGetProperty("enabledMods", out var list) || list.ValueKind != JsonValueKind.Array) return [];

            return list.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .OfType<string>()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public string Render(IReadOnlyList<string> mods, string? currentFileText)
        => JsonSerializer.Serialize(new { enabledMods = mods.Order(StringComparer.Ordinal).ToArray() }, WriteOptions);
}

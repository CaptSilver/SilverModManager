using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;

namespace NexusMods.Backend.Games.Locators;

/// <summary>
/// The Wine settings Heroic keeps for one game in <c>GamesConfig/&lt;appName&gt;.json</c>. Heroic writes the
/// file only once a game has been configured, and native Linux builds have no Wine settings at all.
/// </summary>
/// <param name="WinePrefix">The game's Wine prefix, as written (a host path).</param>
/// <param name="WineBin">The Wine or Proton executable Heroic runs the game with.</param>
/// <param name="WineName">Heroic's name for that runner, such as <c>Wine-GE</c>.</param>
/// <param name="WineType">Heroic's runner kind, such as <c>wine</c> or <c>proton</c>.</param>
internal sealed record HeroicGameSettings(string? WinePrefix, string? WineBin, string? WineName, string? WineType)
{
    /// <summary>
    /// Reads the settings of <paramref name="appName"/> from <paramref name="root"/>; null when the file is
    /// missing, and null with a warning when it cannot be read or is not the JSON Heroic writes.
    /// </summary>
    public static HeroicGameSettings? Read(HeroicConfigRoot root, string appName, ILogger logger)
    {
        var file = root.GameSettingsFile(appName);
        if (!file.FileExists) return null;

        try
        {
            using var stream = file.Read();
            using var document = JsonDocument.Parse(stream);

            // Heroic nests the settings under the app name; tolerate a file that holds them at the top.
            var settings = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(appName, out var nested)
                ? nested
                : document.RootElement;
            if (settings.ValueKind != JsonValueKind.Object) return null;

            var version = settings.TryGetProperty("wineVersion", out var v) && v.ValueKind == JsonValueKind.Object ? v : (JsonElement?)null;
            return new HeroicGameSettings(
                String(settings, "winePrefix"),
                version is { } wine ? String(wine, "bin") : null,
                version is { } named ? String(named, "name") : null,
                version is { } typed ? String(typed, "type") : null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Skipping Heroic's settings for {AppName} at {Path}", appName, file);
            return null;
        }
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

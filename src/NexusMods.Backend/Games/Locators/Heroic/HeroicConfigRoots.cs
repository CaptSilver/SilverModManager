using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;

namespace NexusMods.Backend.Games.Locators;

/// <summary>
/// One Heroic configuration folder: the native install's <c>$XDG_CONFIG_HOME/heroic</c> or the Flatpak's.
/// </summary>
internal sealed record HeroicConfigRoot(AbsolutePath Path, bool IsFlatpak)
{
    public AbsolutePath GameSettingsFile(string appName) => Path.Combine($"GamesConfig/{appName}.json");

    /// <summary>
    /// The GOG games this root lists as installed, as (app name, install path). Empty when the file is missing,
    /// and empty with a warning when it cannot be read, so one broken install does not hide the other's games.
    /// </summary>
    public ImmutableArray<KeyValuePair<string, string>> InstalledGogGames(ILogger logger)
    {
        var file = Path.Combine("gog_store/installed.json");
        if (!file.FileExists) return [];

        try
        {
            using var stream = file.Read();
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Array) return [];

            var games = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
            foreach (var entry in installed.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var appName = entry.TryGetProperty("appName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
                var installPath = entry.TryGetProperty("install_path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
                if (appName is null || installPath is null) continue;
                games.Add(new KeyValuePair<string, string>(appName, installPath));
            }

            return games.ToImmutable();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Skipping Heroic's installed GOG games list at {Path}", file);
            return [];
        }
    }
}

/// <summary>
/// The places Heroic keeps its configuration. A machine can have both installs, each with its own games.
/// </summary>
internal static class HeroicConfigRoots
{
    private const string FlatpakConfig = ".var/app/com.heroicgameslauncher.hgl/config/heroic";

    public static ImmutableArray<HeroicConfigRoot> All(IFileSystem fileSystem) =>
    [
        new HeroicConfigRoot(fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic"), IsFlatpak: false),
        new HeroicConfigRoot(fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(FlatpakConfig), IsFlatpak: true),
    ];
}

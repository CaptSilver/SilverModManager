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
    /// <summary>One game Legendary lists as installed.</summary>
    /// <param name="Platform">Legendary's platform name (<c>Windows</c>, <c>Mac</c>), or null when the entry has none.</param>
    public sealed record EpicInstall(string AppName, string InstallPath, string? Platform);

    /// <summary>One game Nile lists as installed.</summary>
    /// <param name="Version">Nile's version string for the install, or null when the entry has none.</param>
    public sealed record AmazonInstall(string ProductId, string InstallPath, string? Version);

    private AbsolutePath LegendaryConfig => Path.Combine("legendaryConfig/legendary");

    /// <summary>
    /// Legendary's metadata file for an Epic game, which carries the catalog item id.
    /// </summary>
    public AbsolutePath EpicMetadataFile(string appName) => LegendaryConfig.Combine($"metadata/{appName}.json");

    /// <summary>
    /// The Epic games (not their DLC) Legendary lists as installed. Empty when the file is missing, and empty
    /// with a warning when it cannot be read, so one broken install does not hide the other's games.
    /// </summary>
    public ImmutableArray<EpicInstall> InstalledEpicGames(ILogger logger)
    {
        var file = LegendaryConfig.Combine("installed.json");
        if (!file.FileExists) return [];

        try
        {
            using var stream = file.Read();
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];

            var games = ImmutableArray.CreateBuilder<EpicInstall>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var entry = property.Value;
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (entry.TryGetProperty("is_dlc", out var dlc) && dlc.ValueKind == JsonValueKind.True) continue;

                // The key is the app name; the entry repeats it, and the key wins if they ever disagree.
                var installPath = entry.TryGetProperty("install_path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
                var platform = entry.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                if (installPath is null) continue;
                games.Add(new EpicInstall(property.Name, installPath, platform));
            }

            return games.ToImmutable();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Skipping Legendary's installed Epic games list at {Path}", file);
            return [];
        }
    }

    /// <summary>
    /// The catalog item id in Legendary's metadata for <paramref name="appName"/>, or null when the file is
    /// missing or has none.
    /// </summary>
    public string? EpicCatalogItemId(string appName, ILogger logger)
    {
        var file = EpicMetadataFile(appName);
        if (!file.FileExists) return null;

        try
        {
            using var stream = file.Read();
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("metadata", out var metadata)
                || metadata.ValueKind != JsonValueKind.Object) return null;
            return metadata.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Skipping Legendary's metadata for {AppName} at {Path}", appName, file);
            return null;
        }
    }

    /// <summary>
    /// The Amazon games Nile lists as installed. Empty when the file is missing (a Heroic without an Amazon
    /// login has none), and empty with a warning when it cannot be read, so one broken install does not hide
    /// the other's games.
    /// </summary>
    public ImmutableArray<AmazonInstall> InstalledAmazonGames(ILogger logger)
    {
        var file = Path.Combine("nile_config/nile/installed.json");
        if (!file.FileExists) return [];

        try
        {
            using var stream = file.Read();
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var games = ImmutableArray.CreateBuilder<AmazonInstall>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var id = entry.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                var installPath = entry.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var version = entry.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (id is null || installPath is null) continue;
                games.Add(new AmazonInstall(id, installPath, version));
            }

            return games.ToImmutable();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Skipping Nile's installed Amazon games list at {Path}", file);
            return [];
        }
    }

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

using JetBrains.Annotations;
using NexusMods.Sdk.Settings;
using NexusMods.Paths;
using NexusMods.Sdk;

namespace NexusMods.Games.FileHashes;

[PublicAPI]
public record FileHashesServiceSettings : ISettings
{
    /// <summary>
    /// Location where the temporary folder will be stored.
    /// </summary>
    public ConfigurablePath HashDatabaseLocation { get; init; }
    
    /// <summary>
    /// Only checks GitHub for updates this often, in order to avoid API rate limits.
    /// </summary>
    public TimeSpan HashDatabaseUpdateInterval { get; init; } = TimeSpan.FromMinutes(30);
    
    /// <summary>
    /// Never contact GitHub: open the newest database on disk, or extract the embedded one when
    /// there is none. A launch that has to start quickly and work offline sets this.
    /// </summary>
    public bool SkipUpdateCheck { get; init; }

    /// <summary>
    /// The URL to the Github API to get the latest release.
    /// </summary>
    public Uri GithubManifestUrl { get; init; } = new("https://github.com/CaptSilver/game-hashes/releases/latest/download/manifest.json");
    
    /// <summary>
    /// The URL to the GitHub release to download the latest hashes database.
    /// </summary>
    public Uri GameHashesDbUrl { get; init; } = new("https://github.com/CaptSilver/game-hashes/releases/latest/download/game_hashes_db.zip");
    
    public RelativePath ReleaseFilePath { get; init; } = "game-hashes-release.json";
    

    /// <inheritdoc/>
    public static string SettingsId => "SilverModManager.Games.FileHashes.FileHashesServiceSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureDefault(CreateDefault)
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json));
    }

    /// <summary>
    /// Create default.
    /// </summary>
    public static FileHashesServiceSettings CreateDefault(IServiceProvider serviceProvider)
    {
        return new FileHashesServiceSettings
        {
            HashDatabaseLocation = new ConfigurablePath(KnownPath.XDG_DATA_HOME, $"{ApplicationConstants.DataDirectoryName}/FileHashesDatabase"),
        };
    }
}

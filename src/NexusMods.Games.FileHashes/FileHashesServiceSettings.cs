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
    /// Where the store of rows this app writes from the game's own install data lives. It sits beside
    /// <see cref="HashDatabaseLocation"/>, not below it: that directory is pruned down to the newest
    /// downloaded database on every start.
    /// </summary>
    /// <remarks>
    /// The default is set here as well as in <see cref="CreateDefault"/>, because a settings file
    /// saved by an earlier build is deserialized as-is and would otherwise leave this unset.
    /// </remarks>
    public ConfigurablePath LocalBaselineLocation { get; init; } = DefaultLocalBaselineLocation;

    /// <summary>
    /// Keeps the local store in memory instead of on disk. A writable RocksDB takes an exclusive
    /// lock on its directory, so tests and the designer, which can run beside another instance,
    /// cannot share one.
    /// </summary>
    public bool UseInMemoryLocalBaseline { get; init; }

    private static ConfigurablePath DefaultLocalBaselineLocation =>
        new(KnownPath.XDG_DATA_HOME, $"{ApplicationConstants.DataDirectoryName}/LocalVanillaBaseline");
    
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
            LocalBaselineLocation = DefaultLocalBaselineLocation,
        };
    }
}

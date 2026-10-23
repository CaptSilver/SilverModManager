using JetBrains.Annotations;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Thunderstore;

[PublicAPI]
public record ThunderstoreSettings : ISettings
{
    /// <summary>
    /// Where the downloaded package indexes live, one folder per community.
    /// </summary>
    public ConfigurablePath CacheRoot { get; init; }

    /// <summary>
    /// How old a community's cached index may get before opening its page refreshes it.
    /// </summary>
    public TimeSpan RefreshAge { get; init; } = TimeSpan.FromHours(1);

    /// <inheritdoc/>
    public static string SettingsId => "SilverModManager.Networking.Thunderstore.ThunderstoreSettings";

    // No settings-page entry: the defaults are what r2modman-style managers use, and nobody has asked to tune them.
    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureDefault(CreateDefault)
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json));
    }

    public static ThunderstoreSettings CreateDefault(IServiceProvider serviceProvider)
    {
        return new ThunderstoreSettings
        {
            CacheRoot = new ConfigurablePath(KnownPath.XDG_DATA_HOME, $"{ApplicationConstants.DataDirectoryName}/Thunderstore"),
        };
    }
}

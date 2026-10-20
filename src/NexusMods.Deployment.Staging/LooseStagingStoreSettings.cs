using JetBrains.Annotations;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NexusMods.Paths;

namespace NexusMods.Deployment.Staging;

[PublicAPI]
public record LooseStagingStoreSettings : ISettings
{
    /// <summary>
    /// Directory the staged files are stored in. Hardlinking a staged file into a game folder only
    /// works when both are on the same filesystem.
    /// </summary>
    public ConfigurablePath Location { get; init; }

    /// <inheritdoc/>
    public static string SettingsId => "SilverModManager.Deployment.LooseStagingStoreSettings";

    // No settings-page entry: nothing deploys through the store yet.
    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureDefault(CreateDefault)
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json));
    }

    public static LooseStagingStoreSettings CreateDefault(IServiceProvider serviceProvider)
    {
        return new LooseStagingStoreSettings
        {
            Location = new ConfigurablePath(KnownPath.XDG_DATA_HOME, $"{ApplicationConstants.DataDirectoryName}/DataModel/LooseStaging"),
        };
    }
}

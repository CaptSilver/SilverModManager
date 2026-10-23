using System.Text.Json.Serialization;
using JetBrains.Annotations;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using NexusMods.Paths;

namespace SilverModManager.Deployment.Vfs;

[PublicAPI]
public record VfsSettings : ISettings
{
    /// <summary>
    /// Where files the game writes through the mounted folder are kept until they are promoted
    /// into the loadout.
    /// </summary>
    public ConfigurablePath StagingLocation { get; init; }

    /// <summary>
    /// Serves everything with zero lifetimes, so the kernel asks the tree about every access.
    /// A debugging switch: slow, but it rules the kernel's caches out.
    /// </summary>
    public bool DisableCache { get; init; }

    /// <summary>
    /// The most the game may have written before promoting it into the loadout is refused.
    /// </summary>
    public ulong PromotionSizeCapBytes { get; init; } = 16UL * 1024 * 1024 * 1024;

    /// <inheritdoc/>
    public static string SettingsId => "SilverModManager.Deployment.VfsSettings";

    /// <summary>The cache policy the tree is built with.</summary>
    [JsonIgnore]
    public VfsCachePolicy CachePolicy => DisableCache ? VfsCachePolicy.Disabled : VfsCachePolicy.Default;

    // No settings-page entry: nothing mounts the tree yet.
    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureDefault(CreateDefault)
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json));
    }

    public static VfsSettings CreateDefault(IServiceProvider serviceProvider)
    {
        return new VfsSettings
        {
            StagingLocation = new ConfigurablePath(KnownPath.XDG_DATA_HOME, $"{ApplicationConstants.DataDirectoryName}/DataModel/VfsStaging"),
        };
    }
}

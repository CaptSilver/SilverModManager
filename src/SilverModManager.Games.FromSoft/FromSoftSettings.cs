using JetBrains.Annotations;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft;

[PublicAPI]
public record FromSoftSettings : ISettings
{
    /// <summary>
    /// Where the FromSoft games keep what is theirs on the host: mod packages, the mod loader and, later,
    /// per-installation state. It sits outside the game folders, so a game folder never has to be written to.
    /// </summary>
    public ConfigurablePath DataRoot { get; init; }

    /// <inheritdoc/>
    public static string SettingsId => "SilverModManager.Games.FromSoft.FromSoftSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureDefault(CreateDefault)
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json));
    }

    public static FromSoftSettings CreateDefault(IServiceProvider serviceProvider)
    {
        return new FromSoftSettings
        {
            DataRoot = new ConfigurablePath(KnownPath.XDG_DATA_HOME, $"{ApplicationConstants.DataDirectoryName}/FromSoft"),
        };
    }
}

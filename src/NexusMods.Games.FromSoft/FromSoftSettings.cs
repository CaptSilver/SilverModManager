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

    /// <summary>
    /// The Seamless Co-op session password. Stored as plain text in the settings file.
    /// </summary>
    public string CoopPassword { get; init; } = "";

    /// <inheritdoc/>
    public static string SettingsId => "SilverModManager.Games.FromSoft.FromSoftSettings";

    // No settings-page entry: the password is not editable in the UI yet, and a plain-text field that looks like
    // a secret store would be misleading.
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

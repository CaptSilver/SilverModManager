using NexusMods.Sdk.Settings;

namespace NexusMods.Networking.NexusWebApi.Auth;

/// <summary>
/// Nexus sign-in configuration that has no Settings page control.
/// </summary>
/// <remarks>
/// Nexus only accepts OAuth requests from a registered client id, and the id upstream's app
/// uses is not ours to present. It stays empty until a client is registered for this app, and
/// is set by editing the JSON settings file. While it is empty, OAuth sign-in refuses to start.
/// </remarks>
public record NexusAuthSettings : ISettings
{
    public string OAuthClientId { get; init; } = "";

    public static string SettingsId => "SilverModManager.Networking.NexusWebApi.Auth.NexusAuthSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json));
    }
}

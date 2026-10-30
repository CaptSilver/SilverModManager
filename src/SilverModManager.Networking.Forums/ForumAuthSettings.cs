using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Forums;

/// <summary>
/// Client id overrides for testing against a staging OAuth client; there is no Settings page control.
/// </summary>
/// <remarks>
/// The ids the app ships with live in <c>ForumEndpoints</c>. A non-empty value here wins over the built-in one.
/// </remarks>
public record ForumAuthSettings : ISettings
{
    public string LoversLabClientId { get; init; } = "";
    public string VectorPlexusClientId { get; init; } = "";

    public static string SettingsId => "SilverModManager.Networking.Forums.ForumAuthSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json));
    }
}

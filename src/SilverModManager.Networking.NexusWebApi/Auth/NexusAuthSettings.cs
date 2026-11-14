using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.NexusWebApi.Auth;

/// <summary>
/// Nexus sign-in configuration. The Settings page shows the account as a status line and a sign-in or sign-out button
/// on the SSO slug; neither value is edited there.
/// </summary>
/// <remarks>
/// Nexus only accepts OAuth requests from a registered client id, and the id upstream's app
/// uses is not ours to present. It stays empty until a client is registered for this app, and
/// is set by editing the JSON settings file. While it is empty, OAuth sign-in refuses to start.
/// </remarks>
public record NexusAuthSettings : ISettings
{
    public string OAuthClientId { get; init; } = "";

    /// <summary>
    /// The application slug Nexus issues for its single sign-on. Like the client id, it is not ours to borrow from
    /// another application, so it stays empty until one is registered for this app.
    /// </summary>
    public string SsoApplicationSlug { get; init; } = "";

    public static string SettingsId => "SilverModManager.Networking.NexusWebApi.Auth.NexusAuthSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(x => x.SsoApplicationSlug, new PropertyOptions<NexusAuthSettings, string>
            {
                Section = Sections.Accounts,
                DisplayName = "Nexus Mods",
                DescriptionFactory = _ => "Sign in to download from Nexus Mods and to follow your tracked mods.",
            }, new ActionButtonContainerOptions
            {
                CreateSource = services => new AccountButton(services.GetRequiredService<NexusModsAccount>()),
            });
    }
}

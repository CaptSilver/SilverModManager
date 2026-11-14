using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.GitHub;

/// <summary>
/// The GitHub account row on the Settings page. Today it takes a pasted personal access token; the row sits on the
/// device-flow client id so a sign-in button can replace the paste once the app has a client, without a settings
/// migration.
/// </summary>
/// <remarks>The app ships no client id of another application, so the value stays empty until one is registered.</remarks>
public record GitHubAuthSettings : ISettings
{
    public string DeviceFlowClientId { get; init; } = "";

    public static string SettingsId => "SilverModManager.Networking.GitHub.GitHubAuthSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(x => x.DeviceFlowClientId, new PropertyOptions<GitHubAuthSettings, string>
            {
                Section = Sections.Accounts,
                DisplayName = GitHubAccount.Name,
                DescriptionFactory = _ => "A personal access token raises GitHub's rate limit for update checks and release downloads.",
            }, new ActionButtonContainerOptions
            {
                CreateSource = services => new AccountButton(services.GetRequiredService<GitHubAccount>()),
            });
    }
}

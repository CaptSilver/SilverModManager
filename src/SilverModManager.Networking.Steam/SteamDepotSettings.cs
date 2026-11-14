using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Steam;

/// <summary>
/// Steam depot downloads. The Settings page shows the Steam login as an account row on the branch property.
/// </summary>
public record SteamDepotSettings : ISettings
{
    /// <summary>The branch whose manifest depot files are fetched from.</summary>
    public string Branch { get; init; } = "public";

    public static string SettingsId => "SilverModManager.Networking.Steam.SteamDepotSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(x => x.Branch, new PropertyOptions<SteamDepotSettings, string>
            {
                Section = Sections.Accounts,
                DisplayName = SteamDepotAccount.Name,
                DescriptionFactory = _ => "Sign in from a terminal with `silvermodmanager steam login` to fetch game files from Steam.",
            }, new ActionButtonContainerOptions
            {
                CreateSource = services => new AccountButton(services.GetRequiredService<SteamDepotAccount>()),
            });
    }
}

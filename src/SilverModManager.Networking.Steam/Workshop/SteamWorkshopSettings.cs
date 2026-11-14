using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Steam.Workshop;

/// <summary>
/// How long what Steam said about the Workshop items of a game is kept before Steam is asked again. The Settings page
/// shows the Web API key for browsing as an account row on the refresh age.
/// </summary>
public sealed record SteamWorkshopSettings : ISettings
{
    /// <summary>The age at which an answer is asked for again.</summary>
    public TimeSpan RefreshAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>One hour, the age the other sources keep their listings for.</summary>
    public static SteamWorkshopSettings Default { get; } = new();

    public static string SettingsId => "SilverModManager.Networking.Steam.Workshop.SteamWorkshopSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(x => x.RefreshAge, new PropertyOptions<SteamWorkshopSettings, TimeSpan>
            {
                Section = Sections.Accounts,
                DisplayName = SteamWebApiAccount.Name,
                DescriptionFactory = _ => "Your own Steam Web API key lets the app search a game's Workshop. Without it the Workshop page lists only the items you are subscribed to.",
            }, new ActionButtonContainerOptions
            {
                CreateSource = services => new AccountButton(services.GetRequiredService<SteamWebApiAccount>()),
            });
    }
}

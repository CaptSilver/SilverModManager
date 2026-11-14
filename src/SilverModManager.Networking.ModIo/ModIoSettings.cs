using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.ModIo;

/// <summary>
/// How long a mod.io listing is kept before the service is asked again. The Settings page shows the access token as an
/// account row on the refresh age.
/// </summary>
public sealed record ModIoSettings : ISettings
{
    /// <summary>The age at which a listing is asked for again.</summary>
    public TimeSpan RefreshAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>One hour, the age the other sources keep their listings for.</summary>
    public static ModIoSettings Default { get; } = new();

    public static string SettingsId => "SilverModManager.Networking.ModIo.ModIoSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(x => x.RefreshAge, new PropertyOptions<ModIoSettings, TimeSpan>
            {
                Section = Sections.Accounts,
                DisplayName = ModIoAccount.Name,
                DescriptionFactory = _ => "Your own mod.io access token, created at mod.io/me/access with the read and write scopes, lets the app browse mod.io and subscribe to mods for you.",
            }, new ActionButtonContainerOptions
            {
                CreateSource = services => new AccountButton(services.GetRequiredService<ModIoAccount>()),
            });
    }
}

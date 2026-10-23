using NexusMods.Sdk.Settings;

namespace NexusMods.Games.Bethesda.Loot.Masterlists;

/// <summary>
/// Controls whether the LOOT masterlists are refreshed from GitHub.
/// </summary>
/// <remarks>
/// On by default: a stale masterlist sorts worse, and the request carries nothing but the file path.
/// </remarks>
public record LootMasterlistSettings : ISettings
{
    public bool LootMasterlistRefresh { get; init; } = true;

    public static string SettingsId => "SilverModManager.Games.Bethesda.Loot.LootMasterlistSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureProperty(
            x => x.LootMasterlistRefresh,
            new PropertyOptions<LootMasterlistSettings, bool>
            {
                Section = Sections.General,
                DisplayName = "Update LOOT masterlists",
                DescriptionFactory = _ => $"Downloads newer plugin sorting data from {CachingMasterlistStore.Endpoint} (the LOOT masterlists on GitHub) when sorting plugins and about once a day. Only the file path is sent. When off, the masterlists bundled with the app are used.",
            },
            new BooleanContainerOptions()
        );
    }
}

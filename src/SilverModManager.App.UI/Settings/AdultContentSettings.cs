using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Settings;

public record AdultContentSettings : ISettings
{
    /// <summary>Off by default: a fresh install lists nothing a user hasn't asked to see.</summary>
    public bool ShowAdultContent { get; set; }

    public static string SettingsId => "SilverModManager.App.UI.Settings.AdultContentSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureProperty(
            x => x.ShowAdultContent,
            new PropertyOptions<AdultContentSettings, bool>
            {
                Section = Sections.General,
                DisplayName = "Show adult content",
                DescriptionFactory = _ => "When enabled, the store pages list mods, collections and modlists marked as adult content",
            },
            new BooleanContainerOptions()
        );
    }
}

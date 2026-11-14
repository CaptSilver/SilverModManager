using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI;

/// <summary>
/// Controls whether the app claims <c>silvermodmanager://</c> links, the one-click install links GameBanana builds for
/// a mod manager it knows.
/// </summary>
/// <remarks>
/// Off by default: registering takes the scheme from whatever other manager the user runs. The explicit
/// <c>associate-silvermodmanager</c> CLI verb always registers, regardless of this setting. GameBanana only shows the button
/// once the app is registered with the site as a tool; the handler works without that, for a link typed or pasted.
/// </remarks>
public record GameBananaHandlerSettings : ISettings
{
    public bool RegisterAsDefaultHandler { get; init; }

    public static string SettingsId => "SilverModManager.CLI.GameBananaHandlerSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureProperty(
            x => x.RegisterAsDefaultHandler,
            new PropertyOptions<GameBananaHandlerSettings, bool>
            {
                Section = Sections.General,
                DisplayName = "Handle GameBanana (silvermodmanager://) links",
                DescriptionFactory = _ => "Makes this app open GameBanana's one-click install links, which name a mod and one of its files. If another manager has them, this takes them; if that manager takes them back, this app leaves them to it until you turn this setting off and on again or run associate-silvermodmanager.",
            },
            new BooleanContainerOptions()
        );
    }
}

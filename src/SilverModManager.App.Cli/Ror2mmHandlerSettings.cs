using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI;

/// <summary>
/// Controls whether the app claims <c>ror2mm://</c> links, the "Install with Mod Manager" links on Thunderstore.
/// </summary>
/// <remarks>
/// Off by default: registering takes the scheme from whatever other manager the user runs. The explicit
/// <c>associate-ror2mm</c> CLI verb always registers, regardless of this setting.
/// </remarks>
public record Ror2mmHandlerSettings : ISettings
{
    public bool RegisterAsDefaultHandler { get; init; }

    public static string SettingsId => "SilverModManager.CLI.Ror2mmHandlerSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureProperty(
            x => x.RegisterAsDefaultHandler,
            new PropertyOptions<Ror2mmHandlerSettings, bool>
            {
                Section = Sections.General,
                DisplayName = "Handle Thunderstore (ror2mm://) links",
                DescriptionFactory = _ => "Makes this app open Thunderstore's Install with Mod Manager links. If r2modman has them, this takes them; r2modman takes them back each time it starts, and this app then leaves them to it until you turn this setting off and on again or run associate-ror2mm.",
            },
            new BooleanContainerOptions()
        );
    }
}

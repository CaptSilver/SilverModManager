using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>
/// Controls whether the app claims <c>nxm://</c> links on its own.
/// </summary>
/// <remarks>
/// Off by default: registering rewrites the desktop entry and the default scheme handler, which
/// silently takes nxm:// away from whatever other mod manager the user runs. The explicit
/// <c>associate-nxm</c> CLI verb always registers, regardless of this setting.
/// </remarks>
public record NxmHandlerSettings : ISettings
{
    public bool RegisterAsDefaultHandler { get; init; }

    public static string SettingsId => "SilverModManager.Networking.NexusWebApi.NxmHandlerSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureProperty(
            x => x.RegisterAsDefaultHandler,
            new PropertyOptions<NxmHandlerSettings, bool>
            {
                Section = Sections.General,
                DisplayName = "Handle nxm:// links",
                DescriptionFactory = _ => "Makes this app the system handler for nxm:// links, replacing any other mod manager that currently has them. Turning it on registers right away; turning it off does not give the links back to the other app.",
            },
            new BooleanContainerOptions()
        );
    }
}

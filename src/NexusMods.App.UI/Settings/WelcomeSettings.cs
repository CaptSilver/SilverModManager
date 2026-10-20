using NexusMods.Sdk.Settings;

namespace NexusMods.App.UI.Settings;

public record WelcomeSettings : ISettings
{
    public bool HasShownWelcomeMessage { get; set; }

    public static string SettingsId => "SilverModManager.App.UI.Settings.WelcomeSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder) => settingsBuilder;
}

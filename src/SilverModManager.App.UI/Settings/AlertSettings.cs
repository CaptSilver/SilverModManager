using System.Collections.Immutable;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Settings;

public record AlertSettings : ISettings
{
    public ImmutableDictionary<string, bool> AlertStatus { get; set; } = ImmutableDictionary<string, bool>.Empty;

    public bool IsDismissed(string key) => AlertStatus.GetValueOrDefault(key, false);

    public static string SettingsId => "SilverModManager.App.UI.Settings.AlertSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder;
    }
}

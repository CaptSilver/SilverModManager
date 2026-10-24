using System.Collections.Immutable;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Settings;

/// <summary>
/// The order the user dragged the game tiles on the My Games page into.
/// </summary>
public record MyGamesOrderSettings : ISettings
{
    /// <summary>
    /// Keys of the game installations (see <see cref="Pages.MyGames.MyGamesOrder.KeyOf"/>), first to last. Installations
    /// that are not listed (new ones) sort after the listed ones; keys of installations that are gone are ignored.
    /// </summary>
    public ImmutableList<string> GameKeys { get; set; } = ImmutableList<string>.Empty;

    public static string SettingsId => "SilverModManager.App.UI.Settings.MyGamesOrderSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder;
    }
}

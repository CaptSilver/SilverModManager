using System.Collections.Immutable;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Settings;

/// <summary>
/// The sort each store page was last left on, one entry per page and game.
/// </summary>
public record StoreSortSettings : ISettings
{
    /// <summary>Sort keys (see <see cref="Controls.Cards.StoreSortOption.Key"/>) by <c>page/game</c>.</summary>
    public ImmutableDictionary<string, string> Choices { get; set; } = ImmutableDictionary<string, string>.Empty;

    public static string SettingsId => "SilverModManager.App.UI.Settings.StoreSortSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder;
    }
}

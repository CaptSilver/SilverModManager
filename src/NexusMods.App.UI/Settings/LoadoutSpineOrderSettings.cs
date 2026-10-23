using System.Collections.Immutable;
using NexusMods.Sdk.Settings;

namespace NexusMods.App.UI.Settings;

/// <summary>
/// The order the user dragged the loadout icons on the left spine into.
/// </summary>
public record LoadoutSpineOrderSettings : ISettings
{
    /// <summary>
    /// Entity ids of the loadouts, first to last. Loadouts that are not listed
    /// (new ones) sort after the listed ones; ids of deleted loadouts are ignored.
    /// </summary>
    public ImmutableList<ulong> LoadoutIds { get; set; } = ImmutableList<ulong>.Empty;

    public static string SettingsId => "SilverModManager.App.UI.Settings.LoadoutSpineOrderSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder;
    }
}

using System.Collections.Immutable;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Settings;

/// <summary>
/// The order the user dragged each loadout's collections into in the left menu. Display order only: it has no say in
/// which mod wins a conflict.
/// </summary>
public record LoadoutCollectionOrderSettings : ISettings
{
    /// <summary>
    /// Per loadout (keyed by <see cref="LeftMenu.Loadout.LoadoutCollectionOrder.KeyOf"/>), the entity ids of its
    /// collections, first to last. Collections that are not listed (new ones) sort after the listed ones; ids of
    /// collections that are gone are ignored.
    /// </summary>
    public ImmutableDictionary<string, ImmutableList<ulong>> Orders { get; set; } = ImmutableDictionary<string, ImmutableList<ulong>>.Empty;

    public static string SettingsId => "SilverModManager.App.UI.Settings.LoadoutCollectionOrderSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder;
    }
}

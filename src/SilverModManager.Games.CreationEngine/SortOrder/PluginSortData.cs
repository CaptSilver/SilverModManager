using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Games.CreationEngine.SortOrder;

/// <summary>
/// A stored plugin row: its place in the order and its on/off switch, plus the on-disk spelling of the name the
/// base class does not carry.
/// </summary>
public sealed class PluginSortData : SortItemData<SortItemKey<string>>
{
    public PluginSortData(string name, int sortIndex, bool isEnabled, int? earlyLoaderIndex = null, EntityId? rowId = null)
        : base(KeyFor(name), sortIndex)
    {
        Name = name;
        IsEnabled = isEnabled;
        EarlyLoaderIndex = earlyLoaderIndex;
        RowId = rowId;
    }

    /// <summary>The plugin's file name as it is on disk.</summary>
    public string Name { get; set; }

    /// <summary>The user's on/off switch for the plugin.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Where the game loads the plugin among the early loaders, or null when it is not one of them.</summary>
    public int? EarlyLoaderIndex { get; }

    /// <summary>True when the game installed the plugin, so its switch is the game-installed group's and not this row's.</summary>
    public bool IsGameInstalled { get; init; }

    /// <summary>The database entity of the row, null for a row that is not stored yet.</summary>
    public EntityId? RowId { get; }

    /// <summary>The database entity of a row read from the database; throws for a row that is not stored yet.</summary>
    public EntityId StoredRowId => RowId ?? throw new InvalidOperationException($"The row of plugin '{Name}' is not stored yet.");

    public static SortItemKey<string> KeyFor(string name) => new(name.ToLowerInvariant());
}

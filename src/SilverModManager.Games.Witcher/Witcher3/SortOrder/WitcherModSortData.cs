using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Games.Witcher.Witcher3.SortOrder;

/// <summary>
/// A Mods folder's row: its switch and its place in the order, plus the spelling of its name. The place is the one the
/// page is showing, not something read from the database.
/// </summary>
public sealed class WitcherModSortData(string folder, int sortIndex, bool isEnabled, EntityId? rowId = null)
    : SortItemData<SortItemKey<string>>(WitcherModFolders.KeyFor(folder), sortIndex)
{
    /// <summary>The folder name as it is on disk.</summary>
    public string Folder { get; set; } = folder;

    /// <summary>The user's on/off switch for the folder.</summary>
    public bool IsEnabled { get; set; } = isEnabled;

    /// <summary>The database entity of the stored row, null for a folder that has none yet.</summary>
    public EntityId? RowId { get; } = rowId;
}

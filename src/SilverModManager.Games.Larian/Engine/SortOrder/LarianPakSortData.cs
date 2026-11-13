using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Games.Larian.BaldursGate3.SortOrder;

/// <summary>
/// A stored pak row: its place in the order and its on/off switch, plus the on-disk spelling of the name the
/// base class does not carry.
/// </summary>
public sealed class Bg3PakSortData : SortItemData<SortItemKey<string>>
{
    public Bg3PakSortData(string name, int sortIndex, bool isEnabled, EntityId? rowId = null)
        : base(KeyFor(name), sortIndex)
    {
        Name = name;
        IsEnabled = isEnabled;
        RowId = rowId;
    }

    /// <summary>The pak's file name as it is on disk.</summary>
    public string Name { get; set; }

    /// <summary>The user's on/off switch for the pak.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>The database entity of the row, null for a row that is not stored yet.</summary>
    public EntityId? RowId { get; }

    /// <summary>The database entity of a row read from the database; throws for a row that is not stored yet.</summary>
    public EntityId StoredRowId => RowId ?? throw new InvalidOperationException($"The row of pak '{Name}' is not stored yet.");

    public static SortItemKey<string> KeyFor(string name) => new(name.ToLowerInvariant());
}

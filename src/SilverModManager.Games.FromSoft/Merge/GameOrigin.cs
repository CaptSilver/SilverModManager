using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// The installed game's own regulation, held so a fold of several mods can tell which rows of its accumulated side are
/// still the game's. Decrypting a shipped regulation takes seconds, so nothing is read until a row actually asks, and
/// then once for the whole fold.
/// </summary>
public sealed class GameOrigin(byte[] regulation, FromSoftFormatTraits traits)
{
    private byte[]? _payload;
    private Dictionary<int, byte[]>? _entries;
    private readonly Dictionary<int, IReadOnlyDictionary<int, byte[]>?> _tables = [];

    /// <summary>
    /// Takes the payload a fold step already decrypted from the game's file, so the fold pays for the decrypt once.
    /// </summary>
    internal void Adopt(byte[] payload) => _payload ??= payload;

    private byte[] Payload() => _payload ??= Regulation.Unpack(regulation, traits);

    // Reading the binder copies every entry out, so it happens once rather than once per param asked about.
    private Dictionary<int, byte[]> Entries()
    {
        if (_entries is not null) return _entries;
        _entries = [];
        foreach (var entry in Binders.For(traits).Read(Payload())) _entries.TryAdd(entry.Id, entry.Data);
        return _entries;
    }

    /// <summary>The build stamp of the game's regulation.</summary>
    public string Version => Regulation.Version(Payload());

    /// <summary>The game's row, or null when the game has no such row or its param cannot be read.</summary>
    public byte[]? Row(int entryId, int rowId) => Rows(entryId)?.GetValueOrDefault(rowId);

    /// <summary>The game's rows of a param by id, or null when the entry is missing or its param cannot be read (the merge refuses those on its own).</summary>
    public IReadOnlyDictionary<int, byte[]>? Rows(int entryId)
    {
        if (_tables.TryGetValue(entryId, out var known)) return known;
        IReadOnlyDictionary<int, byte[]>? rows = null;
        if (Entries().TryGetValue(entryId, out var data))
        {
            try
            {
                var byId = new Dictionary<int, byte[]>();
                // A repeated id is a distinct record the merge refuses to locate by id anyway; the first copy stands.
                foreach (var row in Param.Read(data).Rows) byId.TryAdd(row.Id, row.Data);
                rows = byId;
            }
            catch (ParamException)
            {
                // Unreadable: no row can be said to be the game's.
            }
        }
        return _tables[entryId] = rows;
    }
}

/// <summary>A row a mod still edits although the game's patch removed it.</summary>
public sealed record RemovedRowEdit(int EntryId, string EntryName, int RowId);

/// <summary>The other side of a param merge edits rows the game removed, so there is no row to carry the edit onto.</summary>
public sealed class RemovedRowEditsException(string mod, IReadOnlyList<RemovedRowEdit> rows)
    : MergeException($"{mod} edits rows the game removed:\n  " + string.Join("\n  ", rows.Select(r => $"{r.EntryName} row {r.RowId}")))
{
    public string Mod { get; } = mod;
    public IReadOnlyList<RemovedRowEdit> Rows { get; } = rows;
}

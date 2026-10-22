using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>
/// Merges two message archives (.msgbnd.dcx) that both claim the same file. Both read and rebuild through the game's
/// binder, so a BND3 game merges the same way a BND4 game does. The result is written in the game's packed format.
/// </summary>
public static class FmgMerge
{
    private const string DefaultBaseName = "the preferred mod";
    private const string DefaultOtherName = "the other mod";

    /// <summary>
    /// Unions the text tables of two archives. Where both define an id, base wins only if it has real text: a null in
    /// base is a structural gap, not an authored value, so it defers to the other side.
    /// </summary>
    /// <exception cref="FmgMergeException">The other archive holds a table base does not; the rebuild cannot add one, and dropping it would report a partial merge as success.</exception>
    /// <param name="notes">Receives a <see cref="TextKept"/> for every id both sides define with different text; null collects nothing.</param>
    /// <param name="baseName">Who owns base, for the notes; a generic name when null.</param>
    /// <param name="otherName">Who owns the other archive, for the notes; a generic name when null.</param>
    public static byte[] Union(
        ReadOnlySpan<byte> @base, ReadOnlySpan<byte> other, FromSoftFormatTraits traits,
        ICollection<IMergeNote>? notes = null, string? baseName = null, string? otherName = null)
    {
        var binder = Binders.For(traits);
        var baseRaw = Dcx.Read(@base, traits);
        var baseEntries = binder.Read(baseRaw);
        var otherTables = binder.Read(Dcx.Read(other, traits)).ToDictionary(e => e.Id, e => Fmg.Read(e.Data));

        var baseIds = baseEntries.Select(e => e.Id).ToHashSet();
        var extra = otherTables.Keys.Where(id => !baseIds.Contains(id)).Order().ToArray();
        if (extra.Length > 0)
            throw new FmgMergeException(
                $"the second archive has entries [{string.Join(", ", extra)}] that the first doesn't; merging would drop them, so refusing rather than reporting a partial merge as success");

        var kept = baseName ?? DefaultBaseName;
        var dropped = otherName ?? DefaultOtherName;
        var replacements = new Dictionary<int, byte[]>();
        foreach (var entry in baseEntries)
        {
            if (!otherTables.TryGetValue(entry.Id, out var incoming) || incoming.Count == 0) continue;

            var table = Fmg.Read(entry.Data);
            var merged = new Dictionary<int, string?>(table);
            var changed = false;
            foreach (var (id, text) in incoming)
            {
                if (table.TryGetValue(id, out var own) && own is not null)
                {
                    // A null on the other side is a gap as well, so base filling it loses nobody's text.
                    if (text is not null && own != text) notes?.Add(new TextKept(entry.Id, id, kept, dropped));
                    continue;
                }
                merged[id] = text;
                changed |= !table.ContainsKey(id) || text is not null;
            }
            if (changed) replacements[entry.Id] = Fmg.Write(merged);
        }
        return Dcx.Write(binder.Rebuild(baseRaw, replacements), traits.PackedWrite);
    }

    /// <summary>
    /// Merges two archives against the vanilla both branched from. Comparing with vanilla answers who changed a row,
    /// which a union cannot: only a row both sides changed, differently, is a conflict, and base's text wins it.
    /// </summary>
    /// <param name="notes">Receives a <see cref="TextKept"/> for every conflict base won; null collects nothing.</param>
    /// <param name="baseName">Who owns base, for the notes; a generic name when null.</param>
    /// <param name="otherName">Who owns the other archive, for the notes; a generic name when null.</param>
    /// <exception cref="FmgMergeException">The three archives hold different tables.</exception>
    public static byte[] ThreeWay(
        ReadOnlySpan<byte> @base, ReadOnlySpan<byte> other, ReadOnlySpan<byte> vanilla, FromSoftFormatTraits traits,
        ICollection<IMergeNote>? notes = null, string? baseName = null, string? otherName = null)
    {
        var kept = baseName ?? DefaultBaseName;
        var dropped = otherName ?? DefaultOtherName;
        var binder = Binders.For(traits);
        var baseRaw = Dcx.Read(@base, traits);
        var baseTables = Tables(binder, baseRaw);
        var otherTables = Tables(binder, Dcx.Read(other, traits));
        var vanillaTables = Tables(binder, Dcx.Read(vanilla, traits));

        BinderEntrySets.RequireSameEntries(
            baseTables.Keys, otherTables.Keys, vanillaTables.Keys, "archives", message => new FmgMergeException(message));

        var replacements = new Dictionary<int, byte[]>();
        foreach (var (entryId, baseTable) in baseTables)
        {
            var otherTable = otherTables[entryId];
            var vanillaTable = vanillaTables[entryId];
            var merged = new Dictionary<int, string?>();
            foreach (var id in baseTable.Keys.Union(otherTable.Keys).Union(vanillaTable.Keys))
            {
                var b = Lookup(baseTable, id);
                var o = Lookup(otherTable, id);
                var v = Lookup(vanillaTable, id);

                object? keep;
                if (Equals(o, b) || Equals(o, v)) keep = b;      // nobody disagrees, or only base moved
                else if (Equals(b, v)) keep = o;                 // only the other side moved
                else
                {
                    // erm's fmg_three_way raises here. The loadout's priority decides a real conflict instead, so the
                    // accumulated side keeps its text and the loss is recorded.
                    keep = b;
                    notes?.Add(new TextKept(entryId, id, kept, dropped));
                }

                if (keep is not Absent) merged[id] = (string?)keep;
            }

            // Fmg.Write dedupes strings vanilla stores twice, so an entry nobody changed is carried over as it is.
            if (!SameTable(merged, baseTable)) replacements[entryId] = Fmg.Write(merged);
        }
        return Dcx.Write(binder.Rebuild(baseRaw, replacements), traits.PackedWrite);
    }

    // An id that is not in a table at all, as opposed to one present with no text. A union cannot tell the two apart,
    // which brings back rows the base deliberately emptied.
    private sealed class Absent
    {
        public static readonly Absent Instance = new();
    }

    private static object? Lookup(Dictionary<int, string?> table, int id) =>
        table.TryGetValue(id, out var text) ? text : Absent.Instance;

    private static bool SameTable(Dictionary<int, string?> a, Dictionary<int, string?> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var text) && text == kv.Value);

    private static Dictionary<int, Dictionary<int, string?>> Tables(IBinder binder, byte[] raw) =>
        binder.Read(raw).ToDictionary(e => e.Id, e => Fmg.Read(e.Data));
}

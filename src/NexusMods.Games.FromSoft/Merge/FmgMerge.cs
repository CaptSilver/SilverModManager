using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>
/// Merges two message archives (.msgbnd.dcx) that both claim the same file. Both read and rebuild through the game's
/// binder, so a BND3 game merges the same way a BND4 game does. The result is written in the game's packed format.
/// </summary>
public static class FmgMerge
{
    /// <summary>
    /// Unions the text tables of two archives. Where both define an id, base wins only if it has real text: a null in
    /// base is a structural gap, not an authored value, so it defers to the other side.
    /// </summary>
    /// <exception cref="FmgMergeException">The other archive holds a table base does not; the rebuild cannot add one, and dropping it would report a partial merge as success.</exception>
    public static byte[] Union(ReadOnlySpan<byte> @base, ReadOnlySpan<byte> other, FromSoftFormatTraits traits)
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

        var replacements = new Dictionary<int, byte[]>();
        foreach (var entry in baseEntries)
        {
            if (!otherTables.TryGetValue(entry.Id, out var incoming) || incoming.Count == 0) continue;

            var table = Fmg.Read(entry.Data);
            var merged = new Dictionary<int, string?>(table);
            var changed = false;
            foreach (var (id, text) in incoming)
            {
                if (table.TryGetValue(id, out var own) && own is not null) continue;
                merged[id] = text;
                changed |= !table.ContainsKey(id) || text is not null;
            }
            if (changed) replacements[entry.Id] = Fmg.Write(merged);
        }
        return Dcx.Write(binder.Rebuild(baseRaw, replacements), traits.PackedWrite);
    }

    /// <summary>
    /// Merges two archives against the vanilla both branched from. Comparing with vanilla answers who changed a row,
    /// which a union cannot: only a row both sides changed, differently, is a conflict.
    /// </summary>
    /// <exception cref="FmgMergeException">The three archives hold different tables, or both sides changed a row to different text.</exception>
    public static byte[] ThreeWay(ReadOnlySpan<byte> @base, ReadOnlySpan<byte> other, ReadOnlySpan<byte> vanilla, FromSoftFormatTraits traits)
    {
        var binder = Binders.For(traits);
        var baseRaw = Dcx.Read(@base, traits);
        var baseTables = Tables(binder, baseRaw);
        var otherTables = Tables(binder, Dcx.Read(other, traits));
        var vanillaTables = Tables(binder, Dcx.Read(vanilla, traits));

        RequireSameEntries(baseTables.Keys, otherTables.Keys, vanillaTables.Keys);

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
                    throw new FmgMergeException(
                        $"entry {entryId} row {id}: both sides changed it and differ; base {Show(b)}, other {Show(o)}, vanilla {Show(v)}");

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

    private static string Show(object? value) => value switch
    {
        Absent => "<absent>",
        null => "None",
        _ => $"'{value}'",
    };

    private static bool SameTable(Dictionary<int, string?> a, Dictionary<int, string?> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var text) && text == kv.Value);

    private static Dictionary<int, Dictionary<int, string?>> Tables(IBinder binder, byte[] raw) =>
        binder.Read(raw).ToDictionary(e => e.Id, e => Fmg.Read(e.Data));

    /// <summary>
    /// Refuses three archives that do not hold the same tables. The rebuild takes its member list from base, so a table
    /// one side lacks has nowhere to go, and "only the other side moved" cannot be told from "base authored this and
    /// vanilla never carried it".
    /// </summary>
    internal static void RequireSameEntries(IEnumerable<int> @base, IEnumerable<int> other, IEnumerable<int> vanilla)
    {
        var sides = new (string Name, HashSet<int> Ids)[] { ("base", [.. @base]), ("other", [.. other]), ("vanilla", [.. vanilla]) };
        var all = sides.SelectMany(s => s.Ids).ToHashSet();
        var odd = all.Where(id => !sides.All(s => s.Ids.Contains(id))).Order().ToArray();
        if (odd.Length == 0) return;

        const int shown = 5;
        var listed = string.Join("; ", odd.Take(shown).Select(id =>
            $"entry {id}: in {string.Join(", ", sides.Where(s => s.Ids.Contains(id)).Select(s => s.Name))}"));
        var more = odd.Length > shown ? $" (and {odd.Length - shown} more)" : "";
        throw new FmgMergeException(
            $"the three archives hold different entries ({listed}{more}); merging would silently drop whichever side is missing from the structural base");
    }
}

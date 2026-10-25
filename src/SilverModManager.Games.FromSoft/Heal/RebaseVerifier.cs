using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>
/// Proves a rebase kept what it was supposed to keep. Two properties, both silent failures otherwise: the output
/// claims the installed build, so a stale merge cannot be mounted over a patched game again; and what each mod
/// authored survives byte for byte, along with every row the new baseline contributed. A mod is held to its own
/// ancestor, the vanilla of the build it was made for: against the installed build's file, every row the patch changed
/// would look like the mod's own.
/// </summary>
public static class RebaseVerifier
{
    private const string DefaultRegulationName = "regulation.bin";

    /// <summary>The problems when every mod was made for the build <paramref name="baseline"/> is the vanilla of; empty when the rebase is sound.</summary>
    /// <param name="merged">The regulation the merge just produced.</param>
    /// <param name="baseline">The vanilla regulation of the installed build the mods were rebased onto.</param>
    /// <param name="mods">Each contributing mod's own regulation.</param>
    /// <param name="live">The installed build; its regulation version is what the output has to claim.</param>
    public static IReadOnlyList<string> Verify(
        byte[] merged, byte[] baseline, IEnumerable<(string ModId, byte[] Regulation)> mods, BuildIdentity live, FromSoftFormatTraits traits)
        => Verify(merged, baseline, mods.Select(m => (m.ModId, m.Regulation, Ancestor: baseline)).ToArray(), live, traits, [], Regulation.Unpack);

    /// <summary>The problems with <paramref name="merged"/>; empty when the rebase is sound.</summary>
    /// <param name="mods">Each contributing mod's own regulation and the vanilla regulation it was made against.</param>
    /// <param name="clashes">What the merge reported keeping the game's bytes over a mod's: warnings, so the lost edit is not a failure.</param>
    public static IReadOnlyList<string> Verify(
        byte[] merged, byte[] baseline, IEnumerable<(string ModId, byte[] Regulation, byte[] Ancestor)> mods, BuildIdentity live,
        FromSoftFormatTraits traits, IReadOnlyList<RowConflict> clashes)
        => Verify(merged, baseline, mods, live, traits, clashes, Regulation.Unpack);

    // The unpack seam exists so a test can count how often a regulation is decrypted.
    internal static IReadOnlyList<string> Verify(
        byte[] merged, byte[] baseline, IEnumerable<(string ModId, byte[] Regulation)> mods, BuildIdentity live, FromSoftFormatTraits traits,
        Func<ReadOnlySpan<byte>, FromSoftFormatTraits, byte[]> unpack)
        => Verify(merged, baseline, mods.Select(m => (m.ModId, m.Regulation, Ancestor: baseline)).ToArray(), live, traits, [], unpack);

    internal static IReadOnlyList<string> Verify(
        byte[] merged, byte[] baseline, IEnumerable<(string ModId, byte[] Regulation, byte[] Ancestor)> mods, BuildIdentity live,
        FromSoftFormatTraits traits, IReadOnlyList<RowConflict> clashes, Func<ReadOnlySpan<byte>, FromSoftFormatTraits, byte[]> unpack)
    {
        var installed = live.RegulationVersion
            ?? throw new ArgumentException("The installed build carries no regulation version, so there is nothing for a rebase to claim.", nameof(live));

        // One decrypt of the merged file answers both its build stamp and its rows. It is never a file read before.
        var payload = unpack(merged, traits);
        var mergedVersion = Regulation.Version(payload);
        var mergedEntries = Binders.For(traits).Read(payload).ToArray();
        var mergedRows = RowsByTable(mergedEntries);

        // Mods of one build share their ancestor's bytes, so each distinct file is decrypted once.
        var cache = new Dictionary<byte[], IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>>>(ReferenceEqualityComparer.Instance);
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> RowsOfFile(byte[] file)
        {
            if (!cache.TryGetValue(file, out var rows)) cache[file] = rows = RowsByTable(Binders.For(traits).Read(unpack(file, traits)));
            return rows;
        }

        var baseRows = RowsOfFile(baseline);
        var modRows = mods.Select(m => (m.ModId, RowsOfFile(m.Regulation), RowsOfFile(m.Ancestor))).ToArray();

        var tableOf = mergedEntries.ToDictionary(e => e.Id, e => TableName(e.Name));
        var acknowledged = clashes
            .Where(c => tableOf.ContainsKey(c.EntryId))
            .Select(c => (tableOf[c.EntryId], c.RowId, c.DroppedMod))
            .ToHashSet();

        return CheckAgainstAncestors(mergedVersion, mergedRows, baseRows, modRows, installed, traits.RegulationFileName ?? DefaultRegulationName, acknowledged);
    }

    private static string TableName(string entryName) => entryName[(entryName.LastIndexOf('\\') + 1)..];

    /// <summary>
    /// Every readable param table's rows by table name then row id. A table that cannot be read is left out rather than
    /// thrown on: the merge never splices rows of an unreadable entry, so there are none to verify.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> RowsByTable(IEnumerable<BinderEntry> entries)
    {
        var tables = new Dictionary<string, IReadOnlyDictionary<int, byte[]>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!entry.Name.EndsWith(".param", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var rows = new Dictionary<int, byte[]>();
                foreach (var row in Param.Read(entry.Data).Rows) rows[row.Id] = row.Data;
                tables[TableName(entry.Name)] = rows;
            }
            catch (ParamException)
            {
                // Not row-spliced by the merge, so nothing to check in it.
            }
        }
        return tables;
    }

    /// <summary>The decision over rows already read, for mods made for the build <paramref name="baseline"/> is the vanilla of.</summary>
    internal static IReadOnlyList<string> Check(
        string mergedVersion,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> merged,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> baseline,
        IEnumerable<(string ModId, IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> Rows)> mods,
        string installedVersion,
        string regulationName = DefaultRegulationName)
        => CheckAgainstAncestors(mergedVersion, merged, baseline, mods.Select(m => (m.ModId, m.Rows, baseline)), installedVersion, regulationName);

    /// <summary>
    /// The decision over rows already read. Rows are compared on their common prefix, as the merge compares them: a few
    /// one-row tables are wider in the game than in a mod, and the extra bytes are not an edit.
    /// </summary>
    /// <param name="clashes">(table, row, mod) the merge reported keeping the game's bytes over the mod's.</param>
    internal static IReadOnlyList<string> CheckAgainstAncestors(
        string mergedVersion,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> merged,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> baseline,
        IEnumerable<(string ModId, IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> Rows, IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> Ancestor)> mods,
        string installedVersion,
        string regulationName = DefaultRegulationName,
        IReadOnlySet<(string Table, int Id, string ModId)>? clashes = null)
    {
        var problems = new List<string>();
        if (mergedVersion != installedVersion)
            problems.Add($"merged {regulationName} claims {mergedVersion}, game is {installedVersion}");

        // A row a mod ships exactly as its own ancestor has it is the mod's silence, not an edit; what the patch did to
        // that row is the merge's business. `changed` is a baseline row some mod either edited or removed, the only way
        // the merged file may lack one. A row the mod's ancestor never had is the patch's contribution as far as that mod
        // goes, so shipping or omitting it says nothing.
        var authored = new Dictionary<(string Table, int Id), Dictionary<string, (byte[] Data, byte[]? Ancestor)>>();
        var changed = new HashSet<(string Table, int Id)>();
        foreach (var (modId, tables, ancestors) in mods)
        {
            foreach (var (table, rows) in tables)
            {
                ancestors.TryGetValue(table, out var ancestorRows);
                foreach (var (id, data) in rows)
                {
                    byte[]? ancestorData = null;
                    if (ancestorRows is not null && ancestorRows.TryGetValue(id, out ancestorData) && ParamRowMerge.ContentEqual(ancestorData, data))
                        continue;
                    changed.Add((table, id));
                    if (!authored.TryGetValue((table, id), out var byMod)) authored[(table, id)] = byMod = new Dictionary<string, (byte[], byte[]?)>();
                    byMod[modId] = (data, ancestorData);
                }
            }
            foreach (var (table, ancestorRows) in ancestors)
            {
                if (!tables.TryGetValue(table, out var rows)) continue;
                foreach (var id in ancestorRows.Keys)
                    if (!rows.ContainsKey(id)) changed.Add((table, id));
            }
        }

        // A row several mods authored was settled by priority and is noted by the merge itself; requiring that all of
        // them survive would fail every legitimate merge.
        foreach (var ((table, id), byMod) in authored.OrderBy(a => a.Key.Table, StringComparer.Ordinal).ThenBy(a => a.Key.Id))
        {
            if (byMod.Count != 1) continue;
            var (modId, (data, ancestorData)) = (byMod.Keys.Single(), byMod.Values.Single());
            if (clashes?.Contains((table, id, modId)) == true) continue;
            var survived = merged.TryGetValue(table, out var mergedRows) && mergedRows.TryGetValue(id, out var mergedData) && EditsSurvive(data, ancestorData, mergedData);
            if (!survived) problems.Add($"{modId}: {table} row {id} did not survive the rebase");
        }

        foreach (var (table, rows) in baseline)
        {
            foreach (var id in rows.Keys)
            {
                if (changed.Contains((table, id))) continue;
                if (!merged.TryGetValue(table, out var mergedRows) || !mergedRows.ContainsKey(id))
                    problems.Add($"baseline {table} row {id} was dropped by the rebase");
            }
        }
        return problems;
    }

    // Byte-level like the merge: every byte the mod moved off its ancestor's row must be the mod's in the merged row. A
    // row the ancestor lacked has no moved bytes to pick out, so the whole row has to match.
    private static bool EditsSurvive(byte[] mod, byte[]? ancestor, byte[]? merged)
    {
        if (merged is null) return false;
        var shared = Math.Min(mod.Length, merged.Length);
        for (var i = 0; i < shared; i++)
        {
            if (ancestor is not null && i < ancestor.Length && mod[i] == ancestor[i]) continue;
            if (mod[i] != merged[i]) return false;
        }
        return true;
    }
}

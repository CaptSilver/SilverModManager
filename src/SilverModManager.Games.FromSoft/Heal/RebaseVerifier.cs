using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>
/// Proves a rebase kept what it was supposed to keep. Two properties, both silent failures otherwise: the output
/// claims the installed build, so a stale merge cannot be mounted over a patched game again; and every row exactly one
/// mod authored survives byte for byte, along with every row the new baseline contributed.
/// </summary>
public static class RebaseVerifier
{
    private const string DefaultRegulationName = "regulation.bin";

    /// <summary>The problems with <paramref name="merged"/>; empty when the rebase is sound.</summary>
    /// <param name="merged">The regulation the merge just produced.</param>
    /// <param name="baseline">The vanilla regulation of the installed build the mods were rebased onto.</param>
    /// <param name="mods">Each contributing mod's own regulation.</param>
    /// <param name="live">The installed build; its regulation version is what the output has to claim.</param>
    public static IReadOnlyList<string> Verify(
        byte[] merged, byte[] baseline, IEnumerable<(string ModId, byte[] Regulation)> mods, BuildIdentity live, FromSoftFormatTraits traits)
        => Verify(merged, baseline, mods, live, traits, Regulation.Unpack);

    // The unpack seam exists so a test can count how often a regulation is decrypted.
    internal static IReadOnlyList<string> Verify(
        byte[] merged, byte[] baseline, IEnumerable<(string ModId, byte[] Regulation)> mods, BuildIdentity live, FromSoftFormatTraits traits,
        Func<ReadOnlySpan<byte>, FromSoftFormatTraits, byte[]> unpack)
    {
        var installed = live.RegulationVersion
            ?? throw new ArgumentException("The installed build carries no regulation version, so there is nothing for a rebase to claim.", nameof(live));

        // One decrypt of the merged file answers both its build stamp and its rows. It is never a file read before.
        var payload = unpack(merged, traits);
        var mergedVersion = Regulation.Version(payload);
        var mergedRows = RowsByTable(Binders.For(traits).Read(payload));

        var modRows = mods.Select(m => (m.ModId, RowsByTable(Binders.For(traits).Read(unpack(m.Regulation, traits))))).ToArray();
        var baseRows = RowsByTable(Binders.For(traits).Read(unpack(baseline, traits)));

        return Check(mergedVersion, mergedRows, baseRows, modRows, installed, traits.RegulationFileName ?? DefaultRegulationName);
    }

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
                tables[entry.Name[(entry.Name.LastIndexOf('\\') + 1)..]] = rows;
            }
            catch (ParamException)
            {
                // Not row-spliced by the merge, so nothing to check in it.
            }
        }
        return tables;
    }

    /// <summary>The decision over rows already read.</summary>
    internal static IReadOnlyList<string> Check(
        string mergedVersion,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> merged,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> baseline,
        IEnumerable<(string ModId, IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> Rows)> mods,
        string installedVersion,
        string regulationName = DefaultRegulationName)
    {
        var problems = new List<string>();
        if (mergedVersion != installedVersion)
            problems.Add($"merged {regulationName} claims {mergedVersion}, game is {installedVersion}");

        // `claimed` is every row any mod's file mentions at all, authored or not. A mod ships a table in full, so a row
        // it left alone shows up identical to the baseline: that is the mod's silence, not a baseline contribution, and
        // its fate is between the mod and the merge. Only a row no mod mentions, because it did not exist when each was
        // built, is purely something the new baseline contributed.
        var authored = new Dictionary<(string Table, int Id), Dictionary<string, byte[]>>();
        var claimed = new HashSet<(string Table, int Id)>();
        foreach (var (modId, tables) in mods)
        {
            foreach (var (table, rows) in tables)
            {
                foreach (var (id, data) in rows)
                {
                    claimed.Add((table, id));
                    if (baseline.TryGetValue(table, out var baseRows) && baseRows.TryGetValue(id, out var baseData) && baseData.AsSpan().SequenceEqual(data))
                        continue;
                    if (!authored.TryGetValue((table, id), out var byMod)) authored[(table, id)] = byMod = new Dictionary<string, byte[]>();
                    byMod[modId] = data;
                }
            }
        }

        // A row several mods authored was settled by priority and is noted by the merge itself; requiring that all of
        // them survive would fail every legitimate merge.
        foreach (var ((table, id), byMod) in authored.OrderBy(a => a.Key.Table, StringComparer.Ordinal).ThenBy(a => a.Key.Id))
        {
            if (byMod.Count != 1) continue;
            var (modId, data) = byMod.Single();
            var survived = merged.TryGetValue(table, out var mergedRows) && mergedRows.TryGetValue(id, out var mergedData) && mergedData.AsSpan().SequenceEqual(data);
            if (!survived) problems.Add($"{modId}: {table} row {id} did not survive the rebase");
        }

        foreach (var (table, rows) in baseline)
        {
            foreach (var id in rows.Keys)
            {
                if (claimed.Contains((table, id))) continue;
                if (!merged.TryGetValue(table, out var mergedRows) || !mergedRows.ContainsKey(id))
                    problems.Add($"baseline {table} row {id} was dropped by the rebase");
            }
        }
        return problems;
    }
}

using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>What the checks of a candidate vanilla regulation found.</summary>
/// <param name="Passed">The candidate may serve as the ancestor of the contributors' merge.</param>
/// <param name="Summary">Names the check that refused it, or what the passed checks found; kept in the provenance.</param>
public sealed record VanillaVerdict(bool Passed, string Summary);

/// <summary>
/// The checks that stand in for a Steam manifest when nothing vouches for a regulation: is it laid out like the
/// game's, does it carry an edit the mods that claim to branch from it did not make, and is it near enough to them to
/// be what they branched from. A wrong ancestor is worse than none, because it silently undoes the patch's edits: a
/// row it lacks reads as an addition the mod wins, and a byte it shares with a mod hides a patch change.
/// </summary>
public static class VanillaValidation
{
    // A mod edits a few rows; a randomizer's output rewrites most of a table. Past this share of the rows the file is
    // somebody's output, not the file the mod started from. The floor keeps a small table from failing on a handful.
    private const double MaxDifferingShare = 0.5;
    private const int DifferingFloor = 20;

    // Enough rows to recognise the problem by, few enough to stay a sentence.
    private const int NamedRows = 5;

    /// <param name="candidate">The regulation under test, its stamp already known to be the contributors' build.</param>
    /// <param name="game">The installed game's regulation, or null when it can't be read; the layout check is then skipped and says so.</param>
    /// <param name="contributors">The mods built for the candidate's build.</param>
    public static VanillaVerdict Check(byte[] candidate, byte[]? game, IReadOnlyList<(string ModId, byte[] Regulation)> contributors, FromSoftFormatTraits traits)
    {
        var summary = new List<string>();

        if (game is null)
        {
            summary.Add("layout not compared: the game's regulation could not be read");
        }
        else
        {
            var problems = LayoutGate.Check(game, [("the candidate", candidate)], traits);
            if (problems.Count > 0) return new VanillaVerdict(false, $"layout: {problems[0]}");
            summary.Add("layout matches the game's");
        }

        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> candidateRows;
        (string ModId, IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> Rows)[] modRows;
        try
        {
            candidateRows = RowsOf(candidate, traits);
            modRows = contributors.Select(c => (c.ModId, RowsOf(c.Regulation, traits))).ToArray();
        }
        catch (Exception exception) when (exception is RegulationException or AesException or DcxException or KrakenException or Bnd4Exception)
        {
            return new VanillaVerdict(false, $"unreadable: {exception.Message}");
        }

        if (modRows.Length >= 2)
        {
            var edits = ForeignEdits(candidateRows, modRows.Select(m => m.Rows).ToArray());
            if (edits.Count > 0)
                return new VanillaVerdict(false, $"consensus: {Describe(edits)} are the same in at least two mods and none matches the candidate, so it carries an edit they did not make");
            summary.Add($"consensus of {modRows.Length} mods found no foreign edit");
        }
        else
        {
            summary.Add("only one mod to compare against, so no consensus check");
        }

        long worst = 0, compared = 0;
        foreach (var (modId, rows) in modRows)
        {
            var (differing, total) = Distance(candidateRows, rows);
            compared = total;
            var limit = Math.Max(DifferingFloor, (long)(total * MaxDifferingShare));
            if (differing > limit)
                return new VanillaVerdict(false, $"closeness: {modId} differs from the candidate on {differing} of {total} rows, too many for the file it was made from");
            worst = Math.Max(worst, differing);
        }
        summary.Add($"closest comparison: at most {worst} of {compared} rows differ");
        return new VanillaVerdict(true, string.Join("; ", summary));
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> RowsOf(byte[] regulation, FromSoftFormatTraits traits)
        => RebaseVerifier.RowsByTable(Binders.For(traits).Read(Regulation.Unpack(regulation, traits)));

    // Rows (table, id) where two or more mods hold the same content, no mod holds the candidate's, and so the candidate
    // looks to carry an edit the mods did not make.
    private static List<(string Table, int Id)> ForeignEdits(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> candidate,
        IReadOnlyList<IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>>> mods)
    {
        var found = new List<(string Table, int Id)>();
        foreach (var (table, rows) in candidate.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var perMod = mods.Select(m => m.GetValueOrDefault(table)).OfType<IReadOnlyDictionary<int, byte[]>>().ToArray();
            if (perMod.Length < 2) continue;

            foreach (var id in rows.Keys.Union(perMod.SelectMany(m => m.Keys)).Order())
            {
                rows.TryGetValue(id, out var inCandidate);
                var held = perMod.Select(m => m.GetValueOrDefault(id)).ToArray();
                // Two mods making the same edit is not evidence against the candidate while another mod still holds its
                // row: a foreign edit in the candidate would show in every mod that left the row alone.
                if (held.Any(h => Same(h, inCandidate))) continue;
                if (held.Any(a => held.Count(b => Same(a, b)) >= 2)) found.Add((table, id));
            }
        }
        return found;
    }

    // Rows the mod and the candidate differ on, of the rows in the tables where a stride comparison means something.
    private static (long Differing, long Total) Distance(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> candidate,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> mod)
    {
        long differing = 0, total = 0;
        foreach (var (table, rows) in candidate)
        {
            if (!Param.StridesComparable(rows.Count, rows.Count)) continue;
            mod.TryGetValue(table, out var modRows);
            foreach (var id in rows.Keys.Union(modRows?.Keys ?? []))
            {
                total++;
                rows.TryGetValue(id, out var inCandidate);
                byte[]? inMod = null;
                modRows?.TryGetValue(id, out inMod);
                if (!Same(inCandidate, inMod)) differing++;
            }
        }
        return (differing, total);
    }

    private static bool Same(byte[]? a, byte[]? b)
        => a is null || b is null ? a is null && b is null : ParamRowMerge.ContentEqual(a, b);

    private static string Describe(List<(string Table, int Id)> rows)
    {
        var named = string.Join(", ", rows.Take(NamedRows).Select(r => $"{r.Table} row {r.Id}"));
        return rows.Count > NamedRows ? $"{named} and {rows.Count - NamedRows} more" : named;
    }
}

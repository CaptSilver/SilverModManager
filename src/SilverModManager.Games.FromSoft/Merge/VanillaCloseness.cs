using System.Security.Cryptography;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;
using EsdFormat = SilverModManager.Games.FromSoft.Formats.Esd;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// How far a candidate vanilla regulation is from the mods that claim to branch from it. Used to choose between two
/// vouched files that carry the same build stamp (a hotfix that did not bump it): the mods were made from one of them,
/// so it differs from them in the fewest rows. Also used for the packed files (talk scripts, message and texture
/// archives), where a library dump and the game's own copy may be different builds.
/// </summary>
internal static class VanillaCloseness
{
    /// <summary>
    /// The number of param rows by which <paramref name="mods"/> differ from <paramref name="candidate"/>, summed over the
    /// mods: a row either side lacks or whose content differs counts once. A candidate that can't be read is as far as
    /// can be, so it loses to any file that can; if every candidate is unreadable the stamp check on the one used says so.
    /// </summary>
    public static long Distance(byte[] candidate, IEnumerable<byte[]> mods, FromSoftFormatTraits traits)
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> baseline;
        try
        {
            baseline = Tables(candidate, traits);
        }
        catch (Exception exception) when (exception is RegulationException or AesException or DcxException or KrakenException or Bnd4Exception)
        {
            return long.MaxValue;
        }

        long total = 0;
        foreach (var mod in mods)
        {
            var rows = Tables(mod, traits);
            foreach (var table in baseline.Keys.Union(rows.Keys))
            {
                baseline.TryGetValue(table, out var vanillaRows);
                rows.TryGetValue(table, out var modRows);
                foreach (var id in (vanillaRows?.Keys ?? []).Union(modRows?.Keys ?? []))
                {
                    var inVanilla = vanillaRows is not null && vanillaRows.TryGetValue(id, out var v) ? v : null;
                    var inMod = modRows is not null && modRows.TryGetValue(id, out var m) ? m : null;
                    if (inVanilla is null || inMod is null || !ParamRowMerge.ContentEqual(inVanilla, inMod)) total++;
                }
            }
        }
        return total;
    }

    /// <summary>
    /// The candidate vanilla copy of the packed file <paramref name="relPath"/> that <paramref name="mods"/> differ from
    /// least, and by how much. The unit follows the file's strategy: state groups for a talk archive, text entries for
    /// a message archive, textures for a texture archive, each summed over the mods. The first of equally near
    /// candidates wins, so the caller lists the likelier ones first. A candidate that can't be read is as far as can be.
    /// </summary>
    /// <returns>
    /// The index of the nearest candidate and its distance; the first candidate and no distance when nothing can be
    /// measured (a path with no strategy, contributors whose files can't be read, or no candidate that can).
    /// </returns>
    public static (int Index, long? Distance) Closest(IReadOnlyList<byte[]> candidates, IReadOnlyList<byte[]> mods, string relPath, FromSoftFormatTraits traits)
    {
        if (candidates.Count == 0) throw new ArgumentException("There is no candidate to choose from.", nameof(candidates));
        if (MergeStrategies.ForPath(relPath, traits, vanillaAvailable: true) is not { } strategy) return (0, null);

        var read = UnitsOf(strategy, traits);
        if (read is null) return (0, null);

        List<IReadOnlyDictionary<string, object?>> modUnits;
        try
        {
            modUnits = mods.Select(read.Read).ToList();
        }
        catch (Exception exception) when (IsFormatFailure(exception))
        {
            return (0, null);
        }

        (int Index, long Distance)? best = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            IReadOnlyDictionary<string, object?> candidate;
            try
            {
                candidate = read.Read(candidates[i]);
            }
            catch (Exception exception) when (IsFormatFailure(exception))
            {
                continue;
            }

            var distance = modUnits.Sum(mod => Differing(candidate, mod, read.Same));
            if (best is null || distance < best.Value.Distance) best = (i, distance);
        }
        return best is { } found ? (found.Index, found.Distance) : (0, null);
    }

    private sealed record UnitReader(Func<byte[], IReadOnlyDictionary<string, object?>> Read, Func<object?, object?, bool> Same);

    // A file as the things a merge compares it by, keyed so the same thing in two files has one key.
    private static UnitReader? UnitsOf(MergeStrategy strategy, FromSoftFormatTraits traits)
    {
        var binder = Binders.For(traits);
        switch (strategy)
        {
            case MergeStrategy.EsdThreeWay:
                return new UnitReader(
                    bytes => binder.Read(Dcx.Read(bytes, traits))
                        .SelectMany(e => EsdFormat.Read(e.Data).Groups.Select(g => (Key: $"{e.Id}/{g.Id}", Value: (object?)g)))
                        .ToDictionary(u => u.Key, u => u.Value),
                    (a, b) => EsdMerge.Same((StateGroup?)a, (StateGroup?)b));
            case MergeStrategy.FmgThreeWay or MergeStrategy.FmgUnion:
                return new UnitReader(
                    bytes => binder.Read(Dcx.Read(bytes, traits))
                        .SelectMany(e => Fmg.Read(e.Data).Select(t => (Key: $"{e.Id}/{t.Key}", Value: (object?)t.Value)))
                        .ToDictionary(u => u.Key, u => u.Value),
                    Equals);
            case MergeStrategy.TpfUnion:
                return new UnitReader(
                    bytes => Tpf.Read(Dcx.Read(bytes, traits)).Textures
                        .GroupBy(t => t.Name).ToDictionary(g => g.Key, g => (object?)Convert.ToHexString(SHA256.HashData(g.First().Data))),
                    Equals);
            case MergeStrategy.PartsbndThreeWay:
                // A repeated id keeps its first member: the merge itself refuses such a bundle, and the measurement
                // only has to rank the candidates.
                return new UnitReader(
                    bytes => binder.Read(Dcx.Read(bytes, traits))
                        .GroupBy(e => e.Id)
                        .ToDictionary(g => g.Key.ToString(), g => (object?)Convert.ToHexString(SHA256.HashData(g.First().Data))),
                    Equals);
            default:
                return null;
        }
    }

    private static long Differing(IReadOnlyDictionary<string, object?> a, IReadOnlyDictionary<string, object?> b, Func<object?, object?, bool> same)
        => a.Keys.Union(b.Keys).Count(key => !(a.TryGetValue(key, out var x) && b.TryGetValue(key, out var y) && same(x, y)));

    private static bool IsFormatFailure(Exception exception) => exception is
        EsdException or FmgException or TpfException or DcxException or KrakenException or Bnd3Exception or Bnd4Exception or AesException;

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> Tables(byte[] regulation, FromSoftFormatTraits traits)
        => RebaseVerifier.RowsByTable(Binders.For(traits).Read(Regulation.Unpack(regulation, traits)));
}

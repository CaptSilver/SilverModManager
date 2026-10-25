using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// How far a candidate vanilla regulation is from the mods that claim to branch from it. Used to choose between two
/// vouched files that carry the same build stamp (a hotfix that did not bump it): the mods were made from one of them,
/// so it differs from them in the fewest rows.
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

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<int, byte[]>> Tables(byte[] regulation, FromSoftFormatTraits traits)
        => RebaseVerifier.RowsByTable(Binders.For(traits).Read(Regulation.Unpack(regulation, traits)));
}

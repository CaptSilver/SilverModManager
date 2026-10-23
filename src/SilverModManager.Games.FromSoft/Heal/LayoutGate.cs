using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>
/// Decides which mod tables cannot be byte-transplanted onto a baseline. A param merge moves whole rows between files,
/// which is only sound while both sides agree on the row stride and on the paramdef those bytes are laid out by. A
/// patch that changes either makes a transplant corrupt data silently, which is the failure worth stopping an apply for.
/// </summary>
public static class LayoutGate
{
    /// <summary>The problems with transplanting each mod's tables onto <paramref name="baseline"/>; empty when it is safe. Every mod is checked, not just the first.</summary>
    public static IReadOnlyList<string> Check(byte[] baseline, IEnumerable<(string ModId, byte[] Regulation)> mods, FromSoftFormatTraits traits)
        => Check(baseline, mods, traits, Regulation.Unpack);

    // The unpack seam exists so a test can count how often a regulation is decrypted.
    internal static IReadOnlyList<string> Check(
        byte[] baseline, IEnumerable<(string ModId, byte[] Regulation)> mods, FromSoftFormatTraits traits,
        Func<ReadOnlySpan<byte>, FromSoftFormatTraits, byte[]> unpack)
        => Check(
            ParamLayouts.Of(unpack(baseline, traits), traits),
            mods.Select(m => (m.ModId, ParamLayouts.Of(unpack(m.Regulation, traits), traits))));

    /// <summary>
    /// The decision over layouts already read. A table the baseline lacks is a merge question, not a transplant-safety
    /// one, so it is skipped; so is a table either side cannot read, since there is nothing to compare and the merge
    /// itself refuses an unreadable entry that differs on both sides.
    /// <para>
    /// The stride of a one-row param is not compared, only its paramdef version: with no gap between rows the derived
    /// width folds in whatever the file keeps before its strings, and the game and a re-saved mod copy round that
    /// differently (<see cref="Param.StridesComparable"/>).
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Check(
        IReadOnlyDictionary<string, Layout?> baseline,
        IEnumerable<(string ModId, IReadOnlyDictionary<string, Layout?> Layouts)> mods)
    {
        var problems = new List<string>();
        foreach (var (modId, layouts) in mods)
        {
            foreach (var (name, layout) in layouts)
            {
                if (!baseline.TryGetValue(name, out var baseLayout)) continue;
                if (baseLayout is null || layout is null) continue;

                if (Param.StridesComparable(layout.Rows, baseLayout.Rows) && layout.Stride != baseLayout.Stride)
                    problems.Add($"{modId}: {name} row stride {layout.Stride} != baseline {baseLayout.Stride} — rows can't be transplanted, this mod needs an update for the new game build");
                else if (layout.ParamdefVersion != baseLayout.ParamdefVersion)
                    problems.Add($"{modId}: {name} paramdef version {layout.ParamdefVersion} != baseline {baseLayout.ParamdefVersion} — field layout moved, this mod needs an update for the new game build");
            }
        }
        return problems;
    }
}

using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// What is known about each <see cref="MergeStrategy"/> without running it, and which strategy a path gets. A path's
/// strategy is a pure function of its name, the game's traits and whether a vanilla copy is on offer, so the merge
/// index can answer for every file in a loadout without reading a byte. The code that performs a merge is bound to a
/// strategy elsewhere.
/// </summary>
public static class MergeStrategies
{
    // The containers the strategies were written for. A file with another extension is not one of them even if it
    // holds the same kind of data.
    private const string FmgContainer = ".msgbnd.dcx";
    private const string TpfContainer = ".tpf.dcx";
    private const string EsdContainer = ".talkesdbnd.dcx";

    /// <summary>The spelling the CLI and UI show, carried over from erm's strategy names.</summary>
    public static string Name(MergeStrategy strategy) => strategy switch
    {
        MergeStrategy.FmgUnion => "fmg-union",
        MergeStrategy.FmgThreeWay => "fmg-3way",
        MergeStrategy.ParamRows => "param-rows",
        MergeStrategy.TpfUnion => "tpf-union",
        MergeStrategy.EsdThreeWay => "esd-3way",
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null),
    };

    /// <summary>Whether the strategy compares each mod against the vanilla file the mods branched from (erm's NEEDS_VANILLA).</summary>
    public static bool NeedsVanilla(MergeStrategy strategy) => strategy is MergeStrategy.FmgThreeWay or MergeStrategy.ParamRows or MergeStrategy.EsdThreeWay;

    /// <summary>
    /// Whether the strategy reports notes about what it kept or dropped. erm's NEEDS_NOTES held only esd-3way and
    /// param-rows; every strategy reports here, the unions included.
    /// </summary>
    public static bool ProducesNotes(MergeStrategy strategy) => strategy switch
    {
        MergeStrategy.FmgUnion or MergeStrategy.FmgThreeWay or MergeStrategy.ParamRows
            or MergeStrategy.TpfUnion or MergeStrategy.EsdThreeWay => true,
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null),
    };

    /// <summary>
    /// Whether a merge that fails stops the whole apply. Only the regulation does: the game cannot run on a copy of it
    /// picked by priority when mods edit different rows, while any other file can be left to me3, which mounts the
    /// package that loadout priority puts last, exactly as for a file the app has no merge for.
    /// </summary>
    public static bool RefusesWhenItFails(MergeStrategy strategy) => strategy is MergeStrategy.ParamRows;

    /// <summary>Whether the first step starts from the game's own file rather than the highest-priority mod's (erm's TAKES_GAME_BASE).</summary>
    public static bool TakesGameBase(MergeStrategy strategy) => strategy is MergeStrategy.ParamRows;

    /// <summary>
    /// The strategy the engine would use for the package-relative <paramref name="relPath"/>, or null when it has none.
    /// Only the whole path equal to the regulation's name counts: a nested file of the same name is not the game's.
    /// </summary>
    public static MergeStrategy? ForPath(string relPath, FromSoftFormatTraits traits, bool vanillaAvailable)
    {
        if (traits.RegulationFileName is { } regulation && relPath.Equals(regulation, StringComparison.OrdinalIgnoreCase))
            return MergeStrategy.ParamRows;
        if (relPath.EndsWith(FmgContainer, StringComparison.OrdinalIgnoreCase))
            return vanillaAvailable ? MergeStrategy.FmgThreeWay : MergeStrategy.FmgUnion;
        if (relPath.EndsWith(TpfContainer, StringComparison.OrdinalIgnoreCase)) return MergeStrategy.TpfUnion;
        if (relPath.EndsWith(EsdContainer, StringComparison.OrdinalIgnoreCase)) return MergeStrategy.EsdThreeWay;
        return null;
    }
}

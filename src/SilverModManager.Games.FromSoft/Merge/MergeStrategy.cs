namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>The ways the app combines several mods' copies of one file into one.</summary>
public enum MergeStrategy
{
    /// <summary>Menu text archives joined by text id, the higher priority keeping its text where both set one.</summary>
    FmgUnion,

    /// <summary>Menu text archives compared against the vanilla file the mods branched from.</summary>
    FmgThreeWay,

    /// <summary>The regulation's param tables, merged row by row.</summary>
    ParamRows,

    /// <summary>Menu texture archives joined by texture name.</summary>
    TpfUnion,

    /// <summary>Talk script archives compared against the vanilla file the mods branched from.</summary>
    EsdThreeWay,
}

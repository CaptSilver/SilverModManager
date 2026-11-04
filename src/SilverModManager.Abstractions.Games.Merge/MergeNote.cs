namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>
/// Something a merge did that the user should hear about: a choice between two edits, or content taken whole. Notes are
/// structured so a caller can group or filter them; <see cref="Describe"/> is the sentence a report prints.
/// </summary>
public interface IMergeNote
{
    /// <summary>The sentence a merge report prints.</summary>
    string Describe();

    /// <summary>The sentence a Health Check card leads with: plain words, the mods by name, no ids the player can't act on.</summary>
    string Summary() => Describe();

    /// <summary>The ids behind <see cref="Summary"/> for someone who wants to look; null when the summary already has all there is.</summary>
    string? Details => null;

    /// <summary>The change this merge dropped, when it dropped one that moving the mods on Rules > File Conflicts could bring back.</summary>
    MergeLoss? Loss => null;
}

/// <summary>A merge kept one mod's edit over another's. Swapping the two mods' order on Rules > File Conflicts swaps the outcome.</summary>
/// <param name="DroppedMod">The mod whose change is not used.</param>
/// <param name="KeptMod">The mod whose change is used instead.</param>
public sealed record MergeLoss(string DroppedMod, string KeptMod)
{
    /// <summary>A loss between two real mods, or null when either side is the generic stand-in a caller that named nobody gets.</summary>
    public static MergeLoss? Between(string droppedMod, string keptMod)
        => droppedMod is MergeNames.OtherMod or MergeNames.PreferredMod or MergeNames.Game
            || keptMod is MergeNames.OtherMod or MergeNames.PreferredMod or MergeNames.Game
            ? null
            : new MergeLoss(droppedMod, keptMod);
}

/// <summary>
/// A note that says which mod's change a merge kept. A fold that merges three or more mods names the mod that leads
/// it, which is not always the one whose change is in the output; the planner uses this to name the right one.
/// </summary>
public interface IWinnerNote : IMergeNote
{
    /// <summary>The mod whose change the merge kept.</summary>
    string Winner { get; }

    /// <summary>The same note with <paramref name="winner"/> as the mod whose change was kept.</summary>
    IMergeNote WithWinner(string winner);
}

/// <summary>What a merge did without choosing between mods. Reports list it; Health Check raises no card for it.</summary>
public interface IMergeDetailNote : IMergeNote;

/// <summary>A note about where a merge's ancestor came from. Reports show it in the source column, not among the choices the merge made.</summary>
public interface IVanillaSourceNote : IMergeNote;

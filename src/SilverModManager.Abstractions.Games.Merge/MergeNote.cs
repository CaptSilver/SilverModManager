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

/// <summary>What a merge did without choosing between mods. Reports list it; Health Check raises no card for it.</summary>
public interface IMergeDetailNote : IMergeNote;

/// <summary>A note about where a merge's ancestor came from. Reports show it in the source column, not among the choices the merge made.</summary>
public interface IVanillaSourceNote : IMergeNote;

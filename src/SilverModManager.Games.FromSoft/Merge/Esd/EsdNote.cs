namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>Why one thing in a merge of state machines could not be carried over as it stood.</summary>
public enum EsdNoteReason
{
    /// <summary>Several of the other mod's states look like this vanilla state, so a change it made there was not applied.</summary>
    UnresolvedInOther,

    /// <summary>The other mod has no state matching this vanilla state.</summary>
    NotInOther,

    /// <summary>Neither mod has a state matching this vanilla state, so no copy survived.</summary>
    NotInEither,

    /// <summary>Several of the preferred mod's states look like this vanilla state, so the other mod's change to it was not applied.</summary>
    UnresolvedInBase,

    /// <summary>The preferred mod deleted this vanilla state.</summary>
    NotInBase,

    /// <summary>Both mods edited this vanilla state differently; the preferred mod's version was kept.</summary>
    BothChanged,

    /// <summary>The other mod's edit jumps somewhere with no counterpart in the merged group.</summary>
    UnplaceableJump,

    /// <summary>A state the other mod added arrived with nothing reaching it.</summary>
    OrphanedGraft,
}

/// <summary>
/// One thing a group's merge could not carry over cleanly. Kept structural so a caller can group or filter notes;
/// <see cref="Describe"/> is the only place that turns one into a sentence.
/// </summary>
/// <param name="Group">The state group the note is about.</param>
/// <param name="State">
/// A vanilla state id for every reason except <see cref="EsdNoteReason.OrphanedGraft"/>, where it is the other mod's
/// own id for a state it added.
/// </param>
/// <param name="Target">
/// Only meaningful for two reasons: the jump target that had no home in the merge (<see cref="EsdNoteReason.UnplaceableJump"/>)
/// and the id a graft landed at (<see cref="EsdNoteReason.OrphanedGraft"/>).
/// </param>
public sealed record EsdNote(long Group, long? State, EsdNoteReason Reason, long? Target = null) : IMergeNote
{
    public string Describe()
    {
        var state = State ?? throw new InvalidOperationException($"a {Reason} note names a state and this one has none");
        return Reason switch
        {
            EsdNoteReason.UnresolvedInOther =>
                $"group {Group}: several of the other mod's states look like vanilla state {state} and nothing separates them, so any change it made there was not applied",
            EsdNoteReason.NotInOther =>
                $"group {Group}: the other mod has no state matching vanilla state {state} -- it either removed or rewrote it, and the preferred mod's copy was kept",
            EsdNoteReason.NotInEither =>
                $"group {Group}: neither mod has a state matching vanilla state {state} -- both removed or rewrote it, so nothing of it survived the merge",
            EsdNoteReason.UnresolvedInBase =>
                $"group {Group}: the other mod changed vanilla state {state}, and several of the preferred mod's states look like it -- the change was not applied rather than applied to the wrong one",
            EsdNoteReason.NotInBase =>
                $"group {Group}: the other mod changed vanilla state {state}, which the preferred mod deleted -- that change was not applied",
            EsdNoteReason.BothChanged =>
                $"group {Group}: both mods changed vanilla state {state} -- kept the preferred mod's version",
            EsdNoteReason.UnplaceableJump =>
                $"group {Group}: the other mod's change to vanilla state {state} jumps to its state {Target}, which has no counterpart in the merged group -- that change was not applied",
            EsdNoteReason.OrphanedGraft =>
                $"group {Group}: the other mod's state {state} came across as state {Target}, but nothing in the merged group reaches it -- whatever it does will not run",
            _ => throw new ArgumentOutOfRangeException(nameof(Reason), Reason, "no sentence for this reason"),
        };
    }
}

using NexusMods.Abstractions.Loadouts.Synchronizers;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>
/// An apply the app will not start because a merged file cannot be made. Deploying the contributors whole instead
/// would let me3 pick one of them silently. The message is also what Health Check shows, so it has to make sense
/// to someone who has not seen the merge run.
/// </summary>
public class MergeRefusedException : SynchronizerPreflightException
{
    public MergeRefusedException(MergeRefusal refusal) : base($"Can't merge {refusal.Describe()}") { }

    /// <param name="reason">Why the merges could not be planned at all, so no path or strategy is to blame.</param>
    public MergeRefusedException(string reason) : base($"Can't apply the loadout's merges: {reason}") { }
}

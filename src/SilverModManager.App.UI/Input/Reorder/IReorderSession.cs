using R3;

namespace SilverModManager.App.UI.Input.Reorder;

/// <summary>
/// A commit that did not go through: where the row really is, whether the failed commit was a cancel, and what the page
/// threw, which belongs in the log.
/// </summary>
public sealed record ReorderFailure(int ActualIndex, bool WasCancel, Exception Error);

/// <summary>
/// A reorder in progress, without its key type: what the pad binding and the hint bar need. The row is "lifted" from
/// <see cref="Start"/>-ing until it is dropped, cancelled or a commit fails.
/// </summary>
public interface IReorderSession
{
    bool IsLive { get; }

    /// <summary>Where the lifted row will land, in displayed order.</summary>
    int TargetIndex { get; }

    /// <summary>Where the row was when it was picked up.</summary>
    int StartIndex { get; }

    /// <summary>Moves the target by <paramref name="delta"/> places, stopping at either end.</summary>
    void Nudge(int delta);

    /// <summary>Puts the row at the target and ends the session.</summary>
    Task Drop();

    /// <summary>Puts the row back where it was picked up and ends the session.</summary>
    Task Cancel();

    /// <summary>Fires when the target changes, including the snap back after a failed commit.</summary>
    Observable<int> TargetChanged { get; }

    /// <summary>Fires once when a commit fails; the session is over by then.</summary>
    Observable<ReorderFailure> Failed { get; }
}

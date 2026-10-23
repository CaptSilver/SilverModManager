namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Lets a caller switch off the check that refuses an apply when a root's recorded backend no longer works
/// there, for the work that moves the root to another backend.
/// </summary>
/// <remarks>
/// Moving a root starts by synchronizing the loadout that is applied so no change is lost, and that
/// synchronize would refuse for the very reason the root is being moved. Under the scope a link backend
/// that cannot link writes private copies instead, which is what a move needs.
/// </remarks>
public static class RecordedRootPreflight
{
    private static readonly AsyncLocal<bool> Suspended = new();

    /// <summary>
    /// Whether recorded roots are checked before an apply on the current flow of execution.
    /// </summary>
    public static bool IsSuspended => Suspended.Value;

    /// <summary>
    /// Suspends the check until the returned scope is disposed.
    /// </summary>
    public static IDisposable Suspend()
    {
        var previous = Suspended.Value;
        Suspended.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Suspended.Value = previous;
    }
}

using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// A one-way flag a stop handler raises so a wait that is polling for a game to leave can tell that the user asked
/// for it to end and hand over what is still running instead of waiting out its settle period.
/// </summary>
[PublicAPI]
public sealed class StopRequest
{
    private int _requested;

    /// <summary>Whether <see cref="Set"/> has been called.</summary>
    public bool Requested => Volatile.Read(ref _requested) != 0;

    /// <summary>Raises the flag. Safe to call from any thread and more than once.</summary>
    public void Set() => Volatile.Write(ref _requested, 1);
}

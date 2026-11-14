namespace SilverModManager.Platform.Gamepad;

/// <summary>
/// Remembers when something last happened, to tell "just now" from "a while ago". The edge is inclusive: an input exactly
/// <c>window</c> after the last one still counts as recent, so the two sides of a hand-over agree on who owns the input.
/// </summary>
public sealed class GamepadPresence(TimeProvider time, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMilliseconds(150);
    private long _touchedAt = long.MinValue;

    public void Touched() => Volatile.Write(ref _touchedAt, time.GetTimestamp());

    public bool IsRecent
    {
        get
        {
            var touched = Volatile.Read(ref _touchedAt);
            return touched != long.MinValue && time.GetElapsedTime(touched) <= _window;
        }
    }
}

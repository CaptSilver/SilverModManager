namespace SilverModManager.Platform.Gamepad;

/// <summary>
/// One thing a pad did. <see cref="At"/> is a monotonic offset from the source's clock, only meaningful next to other events.
/// </summary>
public abstract record GamepadEvent(TimeSpan At);

public sealed record ButtonDown(TimeSpan At, GamepadButton Button) : GamepadEvent(At);

public sealed record ButtonUp(TimeSpan At, GamepadButton Button) : GamepadEvent(At);

/// <summary>A held button's auto-repeat, which only the direction buttons produce.</summary>
public sealed record ButtonRepeat(TimeSpan At, GamepadButton Button) : GamepadEvent(At);

/// <summary>A stick position, each axis in -1..1 with Y positive downwards (SDL's convention).</summary>
public sealed record Stick(TimeSpan At, StickSide Side, float X, float Y) : GamepadEvent(At)
{
    /// <summary>How far an axis has to move before it counts. A stick at rest reports small readings now and then.</summary>
    public const float DeflectionThreshold = 0.25f;

    public bool IsDeflected => Math.Abs(X) >= DeflectionThreshold || Math.Abs(Y) >= DeflectionThreshold;
}

public sealed record PadAdded(TimeSpan At, string Name, GamepadType Type) : GamepadEvent(At);

public sealed record PadRemoved(TimeSpan At, string Name) : GamepadEvent(At);

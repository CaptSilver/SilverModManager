namespace SilverModManager.Platform.Gamepad;

/// <summary>
/// Turns the left stick into D-pad presses with auto-repeat, so menus move the same way with a stick or the D-pad.
/// Pressing needs a deflection past <c>deadZone</c>; letting go needs it to fall under the lower <c>release</c>, so a stick
/// resting near the edge does not chatter.
/// </summary>
public sealed class StickToDirection(
    TimeProvider time,
    float deadZone = 0.5f,
    float release = 0.3f,
    TimeSpan? initialRepeat = null,
    TimeSpan? repeat = null)
{
    private readonly TimeSpan _initialRepeat = initialRepeat ?? TimeSpan.FromMilliseconds(400);
    private readonly TimeSpan _repeat = repeat ?? TimeSpan.FromMilliseconds(100);
    private readonly long _origin = time.GetTimestamp();
    private GamepadButton? _held;
    private float _x;
    private float _y;
    private TimeSpan _nextRepeat;

    // Offsets from construction: a raw timestamp is too large to survive conversion to a double.
    private TimeSpan Now() => time.GetElapsedTime(_origin);

    /// <summary>The direction currently held down, if any.</summary>
    public GamepadButton? Held => _held;

    /// <summary>Takes a new stick position. Only the left stick steers focus, the right one scrolls.</summary>
    public IEnumerable<GamepadEvent> Feed(Stick stick)
    {
        if (stick.Side != StickSide.Left) return [];

        _x = stick.X;
        _y = stick.Y;
        var events = new List<GamepadEvent>();
        var now = Now();

        if (_held is { } held && Magnitude(held) < release)
        {
            events.Add(new ButtonUp(now, held));
            _held = null;
        }

        var (direction, magnitude) = Dominant();
        if (magnitude >= deadZone && direction != _held)
        {
            if (_held is { } previous) events.Add(new ButtonUp(now, previous));
            _held = direction;
            _nextRepeat = now + _initialRepeat;
            events.Add(new ButtonDown(now, direction));
        }

        return events;
    }

    /// <summary>Emits the auto-repeats that came due. Call on a timer: a held stick sends no new positions.</summary>
    public IEnumerable<GamepadEvent> Tick()
    {
        if (_held is not { } held) return [];

        var now = Now();
        var events = new List<GamepadEvent>();
        while (now >= _nextRepeat)
        {
            events.Add(new ButtonRepeat(_nextRepeat, held));
            _nextRepeat += _repeat;
        }

        return events;
    }

    private (GamepadButton Direction, float Magnitude) Dominant() =>
        Math.Abs(_x) >= Math.Abs(_y)
            ? (_x >= 0 ? GamepadButton.DPadRight : GamepadButton.DPadLeft, Math.Abs(_x))
            : (_y >= 0 ? GamepadButton.DPadDown : GamepadButton.DPadUp, Math.Abs(_y));

    private float Magnitude(GamepadButton direction) => direction switch
    {
        GamepadButton.DPadRight => _x,
        GamepadButton.DPadLeft => -_x,
        GamepadButton.DPadDown => _y,
        _ => -_y,
    };
}

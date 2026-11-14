using R3;
using SDL;

namespace SilverModManager.Platform.Gamepad;

/// <summary>
/// Reads pads through SDL on a thread of its own. SDL's event wait is blocking, so the loop wakes every 100 ms to
/// repeat a held stick and to notice a stop request.
/// </summary>
internal sealed class SdlGamepadSource(ISdlEvents sdl, TimeProvider time) : IGamepadSource, IDisposable
{
    private static readonly TimeSpan WaitSlice = TimeSpan.FromMilliseconds(100);
    private const float TriggerPress = 0.5f;
    private const float TriggerRelease = 0.3f;

    private readonly Subject<GamepadEvent> _events = new();
    private readonly object _gate = new();
    private readonly Dictionary<uint, (nint Handle, ConnectedPad Pad)> _open = [];
    private readonly Dictionary<GamepadButton, bool> _triggers = [];
    private readonly long _origin = time.GetTimestamp();
    private StickToDirection _stick = new(time);
    private Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _paused;

    public Observable<GamepadEvent> Events => _events;

    public string? DisabledReason => null;

    public IReadOnlyList<ConnectedPad> Pads
    {
        get
        {
            lock (_gate) return _open.Values.Select(static p => p.Pad).ToList();
        }
    }

    public void Start()
    {
        if (_thread is not null) return;

        _stopping = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "Gamepad input" };
        _thread.Start();
    }

    public void Stop()
    {
        var thread = _thread;
        if (thread is null) return;

        _stopping = true;
        thread.Join();
        _thread = null;
    }

    public void Pause(bool paused)
    {
        _paused = paused;
        // A stick or trigger held while the window went inactive never reports its release, so it would stay "pressed".
        if (!paused) return;
        lock (_gate)
        {
            _stick = new StickToDirection(time);
            _triggers.Clear();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            foreach (var (handle, _) in _open.Values) sdl.CloseGamepad(handle);
            _open.Clear();
        }
        _events.Dispose();
    }

    private void Run()
    {
        while (!_stopping)
        {
            if (sdl.WaitEvent((int)WaitSlice.TotalMilliseconds, out var e)) Handle(e);
            Repeat();
        }
    }

    /// <summary>Handles one event. Public to the tests so they need no thread.</summary>
    internal void Handle(SdlEventView e)
    {
        switch (e.Kind)
        {
            case SdlEventKind.PadAdded: AddPad(e.Which); break;
            case SdlEventKind.PadRemoved: RemovePad(e.Which); break;
            case SdlEventKind.ButtonDown when !_paused && ButtonFor(e.Code) is { } down: _events.OnNext(new ButtonDown(Now(), down)); break;
            case SdlEventKind.ButtonUp when !_paused && ButtonFor(e.Code) is { } up: _events.OnNext(new ButtonUp(Now(), up)); break;
            case SdlEventKind.Axis when !_paused: Axis(e.Code, e.Value); break;
        }
    }

    internal void Repeat()
    {
        if (_paused) return;
        IEnumerable<GamepadEvent> due;
        lock (_gate) due = _stick.Tick().ToList();
        foreach (var repeat in due) _events.OnNext(repeat);
    }

    private TimeSpan Now() => time.GetElapsedTime(_origin);

    private void AddPad(uint id)
    {
        lock (_gate)
        {
            if (_open.ContainsKey(id)) return;
        }

        var handle = sdl.OpenGamepad(id);
        if (handle == 0) return;

        var pad = new ConnectedPad(sdl.NameOf(handle), sdl.TypeOf(handle));
        lock (_gate) _open[id] = (handle, pad);
        _events.OnNext(new PadAdded(Now(), pad.Name, pad.Type));
    }

    private void RemovePad(uint id)
    {
        (nint Handle, ConnectedPad Pad) removed;
        lock (_gate)
        {
            if (!_open.Remove(id, out removed)) return;
        }

        sdl.CloseGamepad(removed.Handle);
        _events.OnNext(new PadRemoved(Now(), removed.Pad.Name));
    }

    private void Axis(int axis, int raw)
    {
        var value = Math.Clamp(raw / 32767f, -1f, 1f);
        switch ((SDL_GamepadAxis)axis)
        {
            case SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX: _leftX = value; PushLeft(); break;
            case SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY: _leftY = value; PushLeft(); break;
            case SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX: _rightX = value; _events.OnNext(new Stick(Now(), StickSide.Right, _rightX, _rightY)); break;
            case SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY: _rightY = value; _events.OnNext(new Stick(Now(), StickSide.Right, _rightX, _rightY)); break;
            case SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER: Trigger(GamepadButton.LT, value); break;
            case SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER: Trigger(GamepadButton.RT, value); break;
        }
    }

    private float _leftX, _leftY, _rightX, _rightY;

    private void PushLeft()
    {
        var stick = new Stick(Now(), StickSide.Left, _leftX, _leftY);
        _events.OnNext(stick);
        List<GamepadEvent> directions;
        lock (_gate) directions = _stick.Feed(stick).ToList();
        foreach (var direction in directions) _events.OnNext(direction);
    }

    private void Trigger(GamepadButton trigger, float value)
    {
        bool pressed, released;
        lock (_gate)
        {
            var down = _triggers.GetValueOrDefault(trigger);
            pressed = !down && value >= TriggerPress;
            released = down && value < TriggerRelease;
            if (pressed) _triggers[trigger] = true;
            else if (released) _triggers[trigger] = false;
        }

        if (pressed) _events.OnNext(new ButtonDown(Now(), trigger));
        else if (released) _events.OnNext(new ButtonUp(Now(), trigger));
    }

    /// <summary>SDL names buttons by position (SOUTH is the bottom face button), which is how <see cref="GamepadButton"/> does too.</summary>
    internal static GamepadButton? ButtonFor(int code) => (SDL_GamepadButton)code switch
    {
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH => GamepadButton.A,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST => GamepadButton.B,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST => GamepadButton.X,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH => GamepadButton.Y,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER => GamepadButton.LB,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER => GamepadButton.RB,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => GamepadButton.DPadUp,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => GamepadButton.DPadDown,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => GamepadButton.DPadLeft,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => GamepadButton.DPadRight,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK => GamepadButton.View,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START => GamepadButton.Menu,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK => GamepadButton.LeftStick,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK => GamepadButton.RightStick,
        _ => null,
    };
}

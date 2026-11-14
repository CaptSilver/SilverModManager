using R3;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.UI.Tests.Input;

/// <summary>A pad source the test pushes events into, standing in for SDL.</summary>
internal sealed class FakeGamepadSource : IGamepadSource
{
    private readonly Subject<GamepadEvent> _events = new();

    public Observable<GamepadEvent> Events => _events;

    public IReadOnlyList<ConnectedPad> Pads { get; set; } = [];

    public string? DisabledReason => null;

    public bool Started { get; private set; }

    public bool Paused { get; private set; }

    public void Start() => Started = true;

    public void Stop() => Started = false;

    public void Pause(bool paused) => Paused = paused;

    public void Emit(GamepadEvent e) => _events.OnNext(e);

    public void Press(GamepadButton button) => Emit(new ButtonDown(TimeSpan.Zero, button));

    public void Release(GamepadButton button) => Emit(new ButtonUp(TimeSpan.Zero, button));
}

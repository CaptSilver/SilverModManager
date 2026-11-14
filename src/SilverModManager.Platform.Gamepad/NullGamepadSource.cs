using R3;

namespace SilverModManager.Platform.Gamepad;

/// <summary>
/// The source used when pads cannot be read. It says why, so the setting can show it instead of silently doing nothing.
/// </summary>
public sealed class NullGamepadSource : IGamepadSource
{
    /// <summary>The reason given when the user turned pad input off, as opposed to it failing.</summary>
    public const string ControllerOffReason = "Controller is off";

    public NullGamepadSource(string disabledReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(disabledReason);
        DisabledReason = disabledReason;
    }

    public string DisabledReason { get; }

    string? IGamepadSource.DisabledReason => DisabledReason;

    public Observable<GamepadEvent> Events => Observable.Empty<GamepadEvent>();

    public IReadOnlyList<ConnectedPad> Pads => [];

    public void Start() { }

    public void Stop() { }

    public void Pause(bool paused) { }
}

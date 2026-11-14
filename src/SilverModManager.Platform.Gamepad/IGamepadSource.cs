using R3;

namespace SilverModManager.Platform.Gamepad;

/// <summary>
/// Where pad events come from. The events arrive on the source's own thread.
/// </summary>
public interface IGamepadSource
{
    Observable<GamepadEvent> Events { get; }

    /// <summary>Starts reading. Does nothing when <see cref="DisabledReason"/> is set.</summary>
    void Start();

    void Stop();

    /// <summary>While paused the source drops every event, so a pad used in another window does nothing here.</summary>
    void Pause(bool paused);

    IReadOnlyList<ConnectedPad> Pads { get; }

    /// <summary>Why no pad will ever be read (library missing, turned off), or null when the source works.</summary>
    string? DisabledReason { get; }
}

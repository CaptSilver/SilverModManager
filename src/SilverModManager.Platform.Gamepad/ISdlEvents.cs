namespace SilverModManager.Platform.Gamepad;

internal enum SdlEventKind
{
    Other,
    PadAdded,
    PadRemoved,
    ButtonDown,
    ButtonUp,
    Axis,
}

/// <summary>
/// The part of an SDL event the source reads. <c>Code</c> is SDL's button or axis number and <c>Value</c> the raw axis
/// reading (-32768..32767); <c>Which</c> is the pad's instance id.
/// </summary>
internal readonly record struct SdlEventView(SdlEventKind Kind, uint Which = 0, int Code = 0, int Value = 0);

/// <summary>
/// The SDL calls the gamepad source makes, so tests can queue events without loading the native library.
/// </summary>
internal interface ISdlEvents
{
    /// <summary>
    /// Starts SDL's gamepad subsystem. Throws <see cref="DllNotFoundException"/> or <see cref="EntryPointNotFoundException"/>
    /// when the library is missing or too old; returns false, with <see cref="LastError"/> set, when SDL itself refuses.
    /// </summary>
    bool Init();

    string LastError { get; }

    string Version { get; }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for the next event; false when none came.</summary>
    bool WaitEvent(int timeoutMs, out SdlEventView e);

    nint OpenGamepad(uint instanceId);

    void CloseGamepad(nint pad);

    GamepadType TypeOf(nint pad);

    string NameOf(nint pad);
}

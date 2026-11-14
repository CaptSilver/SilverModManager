using SDL;

namespace SilverModManager.Platform.Gamepad;

/// <summary>The real SDL3 binding behind <see cref="ISdlEvents"/>.</summary>
internal sealed unsafe class SdlEvents : ISdlEvents
{
    public string LastError { get; private set; } = "";

    public string Version
    {
        get
        {
            var version = SDL3.SDL_GetVersion();
            return $"{version / 1_000_000}.{version / 1_000 % 1_000}.{version % 1_000}";
        }
    }

    public bool Init()
    {
        SdlNativeLoader.Install();

        // Steam Input and the compositor take the pad's focus away; without this SDL stops reporting once the window is
        // not focused. The navigator and the source's Pause decide when the events count, not SDL.
        SDL3.SDL_SetHint(SDL3.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");

        // SDL otherwise takes over SIGINT and SIGTERM and turns them into a quit event, which the source ignores, so the
        // app would stop answering a plain kill or a session logout.
        SDL3.SDL_SetHint(SDL3.SDL_HINT_NO_SIGNAL_HANDLERS, "1");

        if (SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_GAMEPAD)) return true;

        LastError = SDL3.SDL_GetError() ?? "SDL_Init failed";
        return false;
    }

    public bool WaitEvent(int timeoutMs, out SdlEventView view)
    {
        SDL_Event e;
        if (!SDL3.SDL_WaitEventTimeout(&e, timeoutMs))
        {
            view = default;
            return false;
        }

        view = (SDL_EventType)e.type switch
        {
            SDL_EventType.SDL_EVENT_GAMEPAD_ADDED => new SdlEventView(SdlEventKind.PadAdded, (uint)e.gdevice.which),
            SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED => new SdlEventView(SdlEventKind.PadRemoved, (uint)e.gdevice.which),
            SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN => new SdlEventView(SdlEventKind.ButtonDown, (uint)e.gbutton.which, e.gbutton.button),
            SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP => new SdlEventView(SdlEventKind.ButtonUp, (uint)e.gbutton.which, e.gbutton.button),
            SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION => new SdlEventView(SdlEventKind.Axis, (uint)e.gaxis.which, e.gaxis.axis, e.gaxis.value),
            _ => new SdlEventView(SdlEventKind.Other),
        };
        return true;
    }

    public nint OpenGamepad(uint instanceId) => (nint)SDL3.SDL_OpenGamepad((SDL_JoystickID)instanceId);

    public void CloseGamepad(nint pad) => SDL3.SDL_CloseGamepad((SDL_Gamepad*)pad);

    public string NameOf(nint pad) => SDL3.SDL_GetGamepadName((SDL_Gamepad*)pad) ?? "Gamepad";

    public GamepadType TypeOf(nint pad) => SDL3.SDL_GetGamepadType((SDL_Gamepad*)pad) switch
    {
        SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOX360 or SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOXONE => GamepadType.Xbox,
        SDL_GamepadType.SDL_GAMEPAD_TYPE_PS3 or SDL_GamepadType.SDL_GAMEPAD_TYPE_PS4 or SDL_GamepadType.SDL_GAMEPAD_TYPE_PS5 => GamepadType.PlayStation,
        SDL_GamepadType.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_PRO
            or SDL_GamepadType.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_LEFT
            or SDL_GamepadType.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_RIGHT
            or SDL_GamepadType.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_PAIR => GamepadType.Nintendo,
        SDL_GamepadType.SDL_GAMEPAD_TYPE_STEAM => GamepadType.SteamDeck,
        _ => GamepadType.Unknown,
    };
}

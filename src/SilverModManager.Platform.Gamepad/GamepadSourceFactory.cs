using Microsoft.Extensions.Logging;

namespace SilverModManager.Platform.Gamepad;

public static class GamepadSourceFactory
{
    /// <summary>
    /// The SDL source when the library loads, otherwise a <see cref="NullGamepadSource"/> that says why.
    /// </summary>
    public static IGamepadSource Create(ILogger logger, bool enabled) => Create(logger, enabled, new SdlEvents(), TimeProvider.System);

    internal static IGamepadSource Create(ILogger logger, bool enabled, ISdlEvents sdl, TimeProvider time)
    {
        if (!enabled) return new NullGamepadSource(NullGamepadSource.ControllerOffReason);

        try
        {
            if (!sdl.Init())
            {
                var reason = $"SDL could not start: {sdl.LastError}";
                logger.LogWarning("Gamepad input is off. {Reason}", reason);
                return new NullGamepadSource(reason);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            var reason = $"libSDL3 could not be loaded: {ex.Message}";
            logger.LogWarning(ex, "Gamepad input is off. {Reason}", reason);
            return new NullGamepadSource(reason);
        }

        logger.LogInformation("Gamepad input uses SDL {Version}", sdl.Version);
        return new SdlGamepadSource(sdl, time);
    }
}

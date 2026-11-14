using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Platform.Gamepad;
using SilverModManager.Sdk;

namespace SilverModManager.StandardGameLocators.TestHelpers;

/// <summary>
/// A detector with a fixed answer, so a test host does not read the real <c>/proc</c> to decide it runs inside gamescope.
/// </summary>
public sealed class FixedGameModeDetector(bool isGameMode) : IGameModeDetector
{
    public bool IsGameMode => isGameMode;

    public string? Reason => isGameMode ? "test" : null;
}

public static class InputIsolation
{
    /// <summary>
    /// Replaces the SDL pad source with one that reads nothing. Every host that builds the window graph needs this:
    /// the real source turns on SDL's background events and reads the machine's pads, display or no display.
    /// </summary>
    public static IServiceCollection ReplaceGamepadSource(this IServiceCollection services)
    {
        return services.Replace(ServiceDescriptor.Singleton<IGamepadSource>(new NullGamepadSource("tests")));
    }

    /// <summary>
    /// Replaces the detector with a fixed one (not Game Mode unless asked), so the host does not depend on the session it runs in.
    /// </summary>
    public static IServiceCollection ReplaceGameModeDetector(this IServiceCollection services, bool isGameMode = false)
    {
        return services.Replace(ServiceDescriptor.Singleton<IGameModeDetector>(new FixedGameModeDetector(isGameMode)));
    }
}

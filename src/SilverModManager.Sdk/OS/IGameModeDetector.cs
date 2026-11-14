using JetBrains.Annotations;

namespace SilverModManager.Sdk;

/// <summary>
/// Whether the app runs as a Steam shortcut inside gamescope (Steam Deck, Bazzite Game Mode), where there is no
/// browser, no file manager and no desktop notification daemon.
/// </summary>
[PublicAPI]
public interface IGameModeDetector
{
    /// <summary>
    /// True when the session is a gamescope Game Mode session. Fixed for the life of the process.
    /// </summary>
    bool IsGameMode { get; }

    /// <summary>
    /// The signal that decided <see cref="IsGameMode"/>, or null when it is false.
    /// </summary>
    string? Reason { get; }
}

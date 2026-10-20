using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// Knows which game installations this process is currently running a launch for.
/// </summary>
public interface IGameLaunchSession
{
    /// <summary>
    /// True from the moment the launch lock for <paramref name="installation"/> is taken until it is released,
    /// whatever phase the launch is in (preparing, running, tearing down). Writers that change the game folder
    /// or the loadouts behind it refuse to run while this is true.
    /// </summary>
    bool IsRunning(GameInstallation installation);
}

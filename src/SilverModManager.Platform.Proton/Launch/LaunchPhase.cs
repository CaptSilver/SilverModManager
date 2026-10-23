namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// Where a launch is, as recorded next to the installation lock.
/// </summary>
public enum LaunchPhase
{
    /// <summary>Applying the loadout before the game starts.</summary>
    Preparing,

    /// <summary>The game process is running.</summary>
    Running,

    /// <summary>The game has exited and its changes are being ingested.</summary>
    Finishing,
}

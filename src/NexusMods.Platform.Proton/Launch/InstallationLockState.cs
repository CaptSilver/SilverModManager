namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// The contents of an installation lock's state file.
/// </summary>
/// <param name="Pid">Process id of the lock holder.</param>
/// <param name="Loadout">Name of the loadout being launched.</param>
/// <param name="Phase">How far the launch has got.</param>
/// <param name="IsLeftover">
/// True when nobody holds the lock any more, so the file describes a launch that died without cleaning up.
/// </param>
/// <param name="Values">Every key in the file, including ones written by the wrapper.</param>
public sealed record InstallationLockState(
    int Pid,
    string Loadout,
    LaunchPhase Phase,
    bool IsLeftover,
    IReadOnlyDictionary<string, string> Values);

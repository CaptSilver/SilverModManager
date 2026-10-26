namespace SilverModManager.SingleProcess;

/// <summary>
/// How a command run in the main process ended, as far as the client could tell.
/// </summary>
/// <param name="ExitCode">
/// The exit code the verb finished with, or null if the connection closed before the main process reported one
/// (it exited or crashed mid-command). Whatever was rendered before that point is still valid, but the command may
/// not have finished.
/// </param>
public readonly record struct CommandResult(int? ExitCode)
{
    /// <summary>True when the main process reported an exit code.</summary>
    public bool Finished => ExitCode.HasValue;
}

namespace SilverModManager.Wabbajack.Acquire;

public static class ArchiveStateExtensions
{
    /// <summary>
    /// The row waits for the user to fetch the file in a browser. Watching the inbox and the Downloads folder only
    /// makes sense while at least one row is in such a state.
    /// </summary>
    public static bool WaitsForUser(this ArchiveState state) => state is ArchiveState.Browser or ArchiveState.SignIn or ArchiveState.Manual;
}

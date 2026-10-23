namespace NexusMods.SingleProcess;

/// <summary>
/// What the main process is doing. Stored in the sync file next to its pid and port so
/// that clients can tell a main they can talk to from one that has no CLI server.
/// </summary>
public enum MainKind : ushort
{
    /// <summary>The application with its UI and CLI server.</summary>
    Gui = 0,

    /// <summary>A headless main that is running a game. It accepts no clients.</summary>
    HeadlessRun = 1,

    /// <summary>A headless main that is running a single verb. It accepts no clients.</summary>
    HeadlessCli = 2,
}

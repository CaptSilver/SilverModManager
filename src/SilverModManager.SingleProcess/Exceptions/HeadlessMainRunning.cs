namespace NexusMods.SingleProcess.Exceptions;

/// <summary>
/// Thrown when a client finds a live main process that has no CLI server. Unlike
/// <see cref="NoMainProcessStarted"/> the caller must not start another main.
/// </summary>
public class HeadlessMainRunning(MainKind kind, int processId) : Exception(CreateMessage(kind, processId))
{
    public MainKind Kind { get; } = kind;

    public int ProcessId { get; } = processId;

    private static string CreateMessage(MainKind kind, int processId) => kind switch
    {
        MainKind.HeadlessRun => $"A game is running through SilverModManager (pid {processId}). Close the game and try again.",
        _ => $"SilverModManager is busy in another terminal (pid {processId}). Wait for it to finish and try again.",
    };
}

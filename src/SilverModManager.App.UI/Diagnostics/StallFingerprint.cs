namespace SilverModManager.App.UI.Diagnostics;

/// <summary>
/// What the process looked like while the UI thread was not answering.
/// </summary>
/// <param name="Late">How long the probe had been waiting when this was sampled.</param>
/// <param name="StillPending">True for the line written during the stall, false for the one written when the UI answered.</param>
/// <param name="GcPause">GC pause time accumulated between the probe's post and the sample.</param>
/// <param name="Gen2Collections">Gen2 collections between the probe's post and the sample.</param>
/// <param name="PoolThreads">Thread-pool thread count at the sample.</param>
/// <param name="PendingWorkItems">Thread-pool work items queued at the sample.</param>
/// <param name="RunningJobs">Jobs in the running state at the sample.</param>
/// <param name="UiOperation">The operation the UI thread entered last, if any.</param>
/// <param name="UiOperationAge">How long ago that operation was entered.</param>
public sealed record StallFingerprint(
    TimeSpan Late,
    bool StillPending,
    TimeSpan GcPause,
    int Gen2Collections,
    int PoolThreads,
    long PendingWorkItems,
    int RunningJobs,
    string? UiOperation,
    TimeSpan? UiOperationAge);

using SilverModManager.Sdk.Library;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>An archive the run could not download, with the reason as the row of the plan shows it.</summary>
public sealed record DownloadFailure(string Archive, string Reason);

/// <summary>
/// How the latest run of the acquirer is going. The byte count only covers archives that finished: the acquirer does
/// not see how far a transfer in flight has got, so the speed is the average since the run began.
/// </summary>
public sealed record AcquireStatus(
    LibraryFileId? Modlist, bool IsRunning, int Finished, int Total, long BytesFetched, TimeSpan Elapsed, IReadOnlyList<DownloadFailure> Failures)
{
    /// <summary>How many failures a status holds; older ones drop out.</summary>
    public const int FailuresKept = 5;

    /// <summary>Bytes of finished archives per second since the run began, or null while the run is too young to say.</summary>
    public long? BytesPerSecond => Elapsed >= TimeSpan.FromSeconds(1) ? (long)(BytesFetched / Elapsed.TotalSeconds) : null;
}

/// <summary>Which rows of a plan are in which state, grouped the way the command line reports them.</summary>
/// <param name="Have">Archives the library holds, and game files that match.</param>
/// <param name="Automatic">Archives a run downloads without the user.</param>
/// <param name="Browser">Nexus Mods files the user fetches in the browser.</param>
/// <param name="Manual">Archives the user has to get by hand, including sources the app cannot fetch and game files that differ.</param>
public readonly record struct PlanCounts(int Have, int Automatic, int Browser, int Manual)
{
    public static PlanCounts Of(ArchivePlan plan) => new(
        plan.Rows.Count(row => row.State is ArchiveState.Have or ArchiveState.GameFile),
        plan.Rows.Count(row => row.State == ArchiveState.Automatic),
        plan.Rows.Count(row => row.State == ArchiveState.Browser),
        plan.Rows.Count(row => row.State is ArchiveState.Manual or ArchiveState.Unsupported or ArchiveState.GameFileVersion));
}

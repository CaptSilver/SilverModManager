namespace SilverModManager.Wabbajack.Acquire;

/// <summary>How far a run has got: archives finished out of the archives it set out to download.</summary>
public sealed record AcquireProgress(int Finished, int Total);

/// <summary>
/// Gets a plan's archives into the library: downloads the <see cref="ArchiveState.Automatic"/> rows, opens the
/// <see cref="ArchiveState.Browser"/> rows' pages on request, and notices when the user's own downloads arrive.
/// One plan is current at a time.
/// </summary>
public interface IArchiveAcquirer
{
    /// <summary>The current plan with every change so far applied.</summary>
    ArchivePlan Plan { get; }

    /// <summary>Raised after a row of <see cref="Plan"/> changes.</summary>
    event Action<ArchivePlan>? PlanChanged;

    /// <summary>
    /// Raised when the acquirer drops the rows of <see cref="Plan"/> because no page shows it and no run is
    /// downloading it. It is not a <see cref="PlanChanged"/>: a page still listening must not take the empty plan
    /// for the list's.
    /// </summary>
    event Action? PlanForgotten;

    /// <summary>How the latest run is going, or null before the first one. It stays after the run ends, until the next run begins.</summary>
    AcquireStatus? Status { get; }

    /// <summary>Raised when <see cref="Status"/> changes: a run begins, an archive finishes or fails, a run ends.</summary>
    event Action<AcquireStatus>? StatusChanged;

    /// <summary>
    /// Makes <paramref name="plan"/> current and keeps its rows matched against the library, but starts no download,
    /// opens no page and expects no Nexus Mods file. Replaces the current plan.
    /// </summary>
    void Show(ArchivePlan plan);

    /// <summary>
    /// Makes <paramref name="plan"/> current and downloads its Automatic rows. A download that fails turns its row
    /// into a Manual one rather than failing the run. While the run is under way, and while a page is attached, the
    /// rows are matched against the library; afterwards the expected Nexus Mods files stay expected until
    /// <see cref="Release"/> or the next run.
    /// </summary>
    Task Run(ArchivePlan plan, IProgress<AcquireProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Stops the run that is under way, if any, whoever started it. <see cref="Run"/> then ends with a cancellation.</summary>
    void Cancel();

    /// <summary>Opens the pages of up to <paramref name="count"/> Browser rows that have not been opened yet, and returns how many it opened.</summary>
    int OpenNext(int count);

    /// <summary>
    /// Tells the acquirer a page is showing its plan, and looks the rows up once to catch what arrived while no page
    /// was. While at least one page is attached the rows are matched against the library as it changes; dispose the
    /// handle when the page goes away. With no page and no run, the acquirer stops watching the library and
    /// forgets the plan's rows (the Nexus Mods files it expects stay expected), so a page that comes back calls
    /// <see cref="Show"/> again, which looks the rows up afresh.
    /// </summary>
    IDisposable Attach();

    /// <summary>Looks the Browser, Manual and Unsupported rows up in the library again.</summary>
    void Recheck();

    /// <summary>Stops watching the library and forgets which Nexus Mods files are expected.</summary>
    void Release();
}

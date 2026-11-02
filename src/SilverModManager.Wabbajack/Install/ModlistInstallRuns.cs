using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using DynamicData;
using SilverModManager.Abstractions.Games.Modlists;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// An <see cref="InstallModlistJob"/> that has not finished: where it installs, how far it has got, and when it ends.
/// The same job always gives the same instance, so a caller can tell that it already follows a run.
/// </summary>
public sealed class RunningModlistInstall
{
    private readonly IJob _handle;

    internal RunningModlistInstall(InstallModlistJob job, IJob handle)
    {
        Job = job;
        _handle = handle;
        File = LibraryFileId.From(job.Modlist.Id);
    }

    public InstallModlistJob Job { get; }

    /// <summary>The modlist file being installed.</summary>
    public LibraryFileId File { get; }

    public LoadoutId Loadout => Job.TargetLoadout;

    public ModlistInstallProgress Progress => Job.Progress;

    /// <summary>Ends when the job does; faults with the job's exception and is cancelled when the job is.</summary>
    public Task Completion => _handle.WaitAsync();
}

/// <summary>
/// Answers which modlist installs are running, from the job monitor: the one place that knows, so a page that was
/// opened after an install started follows it instead of offering to start another.
/// </summary>
public sealed class ModlistInstallRuns(IJobMonitor monitor) : ILoadoutInstallActivity
{
    private readonly ConditionalWeakTable<InstallModlistJob, RunningModlistInstall> _runs = new();

    /// <summary>The unfinished install of <paramref name="file"/> into <paramref name="loadout"/>, or null.</summary>
    public RunningModlistInstall? Find(LibraryFileId file, LoadoutId loadout)
        => Running().FirstOrDefault(run => run.Loadout == loadout && run.File == file);

    /// <summary>The unfinished install of <paramref name="file"/>, whichever loadout it writes into, or null.</summary>
    public RunningModlistInstall? FindOfFile(LibraryFileId file) => Running().FirstOrDefault(run => run.File == file);

    /// <summary>The unfinished install into <paramref name="loadout"/>, whichever modlist it installs, or null.</summary>
    public RunningModlistInstall? FindInto(LoadoutId loadout) => Running().FirstOrDefault(run => run.Loadout == loadout);

    /// <summary>The run for a job that was just begun, which the monitor lists as soon as it exists.</summary>
    public RunningModlistInstall Of(IJobTask<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly> task)
        => For(task.JobDefinition, task.Job);

    /// <exception cref="InstallAlreadyRunningException">An install into <paramref name="loadout"/> has not finished.</exception>
    /// <exception cref="LoadoutApplyRunningException"><paramref name="loadout"/> is being applied.</exception>
    public void EnsureNoneInto(LoadoutId loadout)
    {
        if (FindInto(loadout) is not null) throw new InstallAlreadyRunningException();

        // The other direction of the guard in the synchronizer: an apply that is already past it must not have the
        // loadout written under it.
        if (monitor.Snapshot().Any(job => job.Status.IsUnfinished() && job.Definition is SynchronizeLoadoutJob sync && sync.LoadoutId == loadout))
            throw new LoadoutApplyRunningException();
    }

    /// <inheritdoc />
    public bool IsInstallingInto(LoadoutId loadout) => FindInto(loadout) is not null;

    /// <inheritdoc />
    /// <remarks>Counts the same jobs as <see cref="IsInstallingInto"/>: the Apply button must not come on while the check would refuse.</remarks>
    public IObservable<bool> ObserveInstallingInto(LoadoutId loadout)
        => monitor.GetObservableChangeSet<InstallModlistJob>()
            .FilterOnObservable(job => job.ObservableStatus.Select(status => status.IsUnfinished()))
            .QueryWhenChanged(query => query.Items.Any(job => job.Definition is InstallModlistJob definition && definition.TargetLoadout == loadout))
            .Prepend(IsInstallingInto(loadout))
            .DistinctUntilChanged();

    private IEnumerable<RunningModlistInstall> Running()
    {
        foreach (var job in monitor.Snapshot())
        {
            // A job the monitor has only just begun has no status yet, so unfinished is everything but the three ends.
            if (!job.Status.IsUnfinished()) continue;
            if (job.Definition is InstallModlistJob definition) yield return For(definition, job);
        }
    }

    private RunningModlistInstall For(InstallModlistJob definition, IJob handle)
        => _runs.GetValue(definition, key => new RunningModlistInstall(key, handle));
}

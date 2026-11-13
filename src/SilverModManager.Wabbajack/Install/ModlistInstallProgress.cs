using System.Reactive.Subjects;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts.Install;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Install;

public enum InstallPhase { Check, Resolve, Write, Order }

/// <summary>
/// How far an <see cref="InstallModlistJob"/> has got. The job updates it from whichever thread it runs on; observers
/// of <see cref="Changes"/> see every snapshot in the order it was produced.
/// </summary>
public sealed class ModlistInstallProgress
{
    private readonly object _lock = new();
    private readonly BehaviorSubject<Snapshot> _subject = new(Snapshot.Empty);
    // Every archive being built, by its number, with when it last reported. Several build at once, so the snapshot's
    // single Archive slot is only the one that reported last; this is where the others keep their counts.
    private readonly Dictionary<int, (ArchiveBuild Build, long Touched)> _building = new();
    private long _touches;
    private int _finished;
    private readonly ModInstallRows _mods = new();
    private readonly Dictionary<string, Folder> _folders = new();
    private int _installed;
    private int _failed;

    /// <summary>What the install knows of one mod folder besides what its row shows.</summary>
    private sealed class Folder(ModInstallRow row, ModFolderPlan plan)
    {
        public ModInstallRow Row { get; } = row;
        public ModFolderPlan Plan { get; } = plan;

        /// <summary>The numbers of this folder's archives still being built; a folder can build two at once.</summary>
        public HashSet<int> Builds { get; } = [];

        /// <summary>The one of <see cref="Builds"/> still running that reported last, which the row shows.</summary>
        public ArchiveBuild? Building { get; set; }
    }

    /// <summary>
    /// One row per mod folder, empty until <see cref="SetMods"/>. It is the same object for the whole run, so a page
    /// that attached during the check phase gets the rows when they are added.
    /// </summary>
    public IModInstallRows Mods => _mods;

    /// <summary>
    /// A Bethesda archive the Write phase is building. The archive is one item of the phase however long it takes, so
    /// this carries its own count of the files packed into it, which moves while the item is still unfinished.
    /// </summary>
    /// <param name="Number">Which archive this is of <paramref name="Count"/>, in the order the builds started. It is also the key the model tracks the build by, so two archives with one file name stay apart.</param>
    public sealed record ArchiveBuild(string Name, int Number, int Count, int DoneFiles, int TotalFiles)
    {
        /// <summary>The line the page and the log show for this build.</summary>
        public string Describe(IFormatProvider culture)
            => string.Create(culture, $"Building archive {Number} of {Count}: {Name} ({DoneFiles:N0} of {TotalFiles:N0} files)");
    }

    /// <summary>
    /// How the archive builds stand. <paramref name="Shown"/> is the oldest build still running, so the name on the page
    /// stays put until that archive finishes instead of hopping to whichever build reported last.
    /// </summary>
    /// <param name="Done">Archives that have finished building.</param>
    /// <param name="Running">Archives building right now.</param>
    /// <param name="Count">Archives the list builds in all.</param>
    public sealed record ArchiveBuilds(int Done, int Running, int Count, ArchiveBuild Shown);

    /// <param name="Archive">The archive being built, or null when none is; with several building at once, the one that reported last.</param>
    /// <param name="Builds">Where the archive builds stand, or null when none is building. The page shows this; <paramref name="Archive"/> is what the log follows.</param>
    /// <param name="ArchiveItems">How much of the phase's items the archives still being built amount to, from the files packed so far; each is less than one because the archive's last step follows its last file. An archive's item is counted by <see cref="Advance"/> when the in-order writer records it, which can be after the archive ends.</param>
    /// <param name="DroppedCount">Paths the list has that the app never installs (Mod Organizer's own files, other profiles, seeds); they leave no skip row.</param>
    /// <param name="ExtraCount">Files of tool folders, which the loadout does not deploy.</param>
    /// <param name="EstimatedExtractedSize">What the archives need on disk once the library has extracted them.</param>
    /// <param name="IsEstimateLowerBound">The estimate counts an archive that is not in the library yet by its download size.</param>
    public sealed record Snapshot(
        InstallPhase Phase,
        int DoneItems,
        int TotalItems,
        long DoneBytes,
        long TotalBytes,
        int DroppedCount,
        int ExtraCount,
        Size EstimatedExtractedSize,
        bool IsEstimateLowerBound,
        ArchiveBuild? Archive = null,
        double ArchiveItems = 0,
        ArchiveBuilds? Builds = null)
    {
        public static readonly Snapshot Empty = new(InstallPhase.Check, 0, 0, 0, 0, 0, 0, Size.From(0), false);
    }

    public Snapshot Current => _subject.Value;

    public IObservable<Snapshot> Changes => _subject;

    public InstallPhase Phase => Current.Phase;
    public int DoneItems => Current.DoneItems;
    public int TotalItems => Current.TotalItems;
    public long DoneBytes => Current.DoneBytes;
    public long TotalBytes => Current.TotalBytes;
    public int DroppedCount => Current.DroppedCount;
    public int ExtraCount => Current.ExtraCount;
    public Size EstimatedExtractedSize => Current.EstimatedExtractedSize;
    public bool IsEstimateLowerBound => Current.IsEstimateLowerBound;

    /// <summary>Starts <paramref name="phase"/> with its own totals; the done counts restart.</summary>
    public void Enter(InstallPhase phase, int totalItems, long totalBytes)
        => Update(s => s with { Phase = phase, DoneItems = 0, TotalItems = totalItems, DoneBytes = 0, TotalBytes = totalBytes });

    public void Advance(int items, long bytes) => Advance(null, items, bytes);

    /// <summary>Counts <paramref name="items"/> done and, when <paramref name="folder"/> is a mod folder of this run, moves its row.</summary>
    public void Advance(string? folder, int items, long bytes)
    {
        lock (_lock)
        {
            var next = _subject.Value with { DoneItems = _subject.Value.DoneItems + items, DoneBytes = _subject.Value.DoneBytes + bytes };
            _subject.OnNext(next);
            if (folder is not null && _folders.TryGetValue(folder, out var known)) AdvanceRow(known, next.Phase, items);
        }
    }

    /// <summary>Gives the page one row per folder, in the order of their index. Called once per run.</summary>
    public void SetMods(IEnumerable<ModFolderPlan> folders)
    {
        lock (_lock)
        {
            var rows = new List<ModInstallRow>();
            foreach (var plan in folders.OrderBy(f => f.Index))
            {
                var row = new ModInstallRow(plan.Folder, plan.Folder, plan.Index, StartOf(plan));
                _folders[plan.Folder] = new Folder(row, plan);
                rows.Add(row);
                if (row.Current.State == ModInstallState.Installed) _installed++;
            }
            _mods.Add(rows);
            PublishTotals();
        }
    }

    /// <summary>
    /// Marks the folder failed with <paramref name="reason"/>. A skip the list asked for is no failure, a folder that
    /// already failed keeps its first reason, and a folder with no row is not this run's.
    /// </summary>
    public void MarkFailed(string folder, SkipKind kind, string reason)
    {
        if (kind.IsByDesign()) return;
        lock (_lock)
        {
            if (!_folders.TryGetValue(folder, out var known) || known.Row.Current.State == ModInstallState.Failed) return;
            if (known.Row.Current.State == ModInstallState.Installed) _installed--;
            known.Row.Set(known.Row.Current with { State = ModInstallState.Failed, Reason = reason, Detail = null });
            _failed++;
            PublishTotals();
        }
    }

    /// <summary>
    /// Tells the row of <paramref name="folder"/> that archive <paramref name="number"/> (see <see cref="BeginArchive"/>)
    /// is its own and has just started, packed a file or ended. The counts come from the archive's own bookkeeping, so
    /// files packed in parallel cannot show out of order; the row shows whichever of the folder's running archives
    /// reported last, the same rule as the snapshot's slot, and nothing once they have all ended.
    /// </summary>
    public void MarkArchiveBuild(string folder, int number)
    {
        lock (_lock)
        {
            if (!_folders.TryGetValue(folder, out var known)) return;
            known.Builds.Add(number);
            known.Builds.RemoveWhere(n => !_building.ContainsKey(n));
            known.Building = known.Builds.Count == 0 ? null : known.Builds.Select(n => _building[n]).MaxBy(b => b.Touched).Build;
            if (known.Row.Current.State == ModInstallState.Failed) return;
            known.Row.Set(known.Row.Current with { Detail = DetailOf(known) });
        }
    }

    private static ModInstallRowState StartOf(ModFolderPlan plan)
    {
        if (plan.AlreadyDone >= plan.Total) return new ModInstallRowState(ModInstallState.Installed, plan.Total, plan.Total, plan.Total);
        if (plan.AlreadyDone > 0) return new ModInstallRowState(ModInstallState.Installing, plan.AlreadyDone, plan.AlreadyDone, plan.Total);
        return new ModInstallRowState(ModInstallState.Waiting, 0, 0, plan.Total);
    }

    private void AdvanceRow(Folder folder, InstallPhase phase, int items)
    {
        var state = folder.Row.Current;
        if (state.State is ModInstallState.Failed or ModInstallState.Installed) return;
        if (phase == InstallPhase.Resolve)
            state = state with { State = ModInstallState.Extracting, Resolved = state.Resolved + items };
        else if (phase == InstallPhase.Write)
            state = state with { State = ModInstallState.Installing, Written = state.Written + items };
        else
            return;

        if (state.State == ModInstallState.Installing && state.Written >= state.Total)
        {
            folder.Row.Set(state with { State = ModInstallState.Installed, Detail = null });
            _installed++;
            PublishTotals();
            return;
        }
        folder.Row.Set(state with { Detail = DetailOf(folder, state) });
    }

    private static string? DetailOf(Folder folder) => DetailOf(folder, folder.Row.Current);

    private static string? DetailOf(Folder folder, ModInstallRowState state)
    {
        if (folder.Building is { } build) return $" · building {build.Name} ({build.DoneFiles:N0} of {build.TotalFiles:N0})";
        var plan = folder.Plan;
        if (state.State != ModInstallState.Installing || plan.LaterPasses <= 0 || state.Written < plan.Total - plan.LaterPasses) return null;
        return plan.LaterPasses == plan.MergePasses ? " · waiting for the merge pass" : " · waiting for the archive pass";
    }

    // The totals move only when a row enters Installed or Failed, so a long run does not recount 3,000 rows per file.
    private void PublishTotals() => _mods.SetTotals(new ModInstallTotals(_folders.Count, _installed, _failed));

    public void AddDropped(int count) => Update(s => s with { DroppedCount = s.DroppedCount + count });

    public void AddExtras(int count) => Update(s => s with { ExtraCount = s.ExtraCount + count });

    public void SetEstimate(Size size, bool isLowerBound)
        => Update(s => s with { EstimatedExtractedSize = size, IsEstimateLowerBound = isLowerBound });

    public void BeginArchive(string name, int number, int count, int totalFiles)
        => ChangeArchives(() => _building[number] = (new ArchiveBuild(name, number, count, 0, totalFiles), ++_touches));

    /// <summary>Counts one more file packed into archive <paramref name="number"/>, which makes it the one the snapshot shows.</summary>
    public void AdvanceArchive(int number)
        => ChangeArchives(() =>
        {
            if (_building.TryGetValue(number, out var running))
                _building[number] = (running.Build with { DoneFiles = running.Build.DoneFiles + 1 }, ++_touches);
        });

    public void EndArchive(int number)
        => ChangeArchives(() =>
        {
            if (_building.Remove(number)) _finished++;
        });

    // The slot and the partial items are worked out from every running build each time, so one ending can never
    // hide another.
    private void ChangeArchives(Action change)
    {
        lock (_lock)
        {
            change();
            var shown = _building.Count == 0 ? null : _building.Values.MaxBy(b => b.Touched).Build;
            var items = _building.Values.Sum(b => b.Build.TotalFiles <= 0 ? 0 : Math.Min((double)b.Build.DoneFiles / b.Build.TotalFiles, MostOfAnItem));
            var builds = shown is null
                ? null
                : new ArchiveBuilds(_finished, _building.Count, shown.Count, _building.Values.MinBy(b => b.Build.Number).Build);
            _subject.OnNext(_subject.Value with { Archive = shown, ArchiveItems = items, Builds = builds });
        }
    }

    private const double MostOfAnItem = 0.99;

    private void Update(Func<Snapshot, Snapshot> change)
    {
        lock (_lock) _subject.OnNext(change(_subject.Value));
    }
}

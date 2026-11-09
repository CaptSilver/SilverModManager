using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// Writes an install's progress to the log at Information: one line when a phase starts and one each time a phase
/// passes another 5%, so a long install can be followed in the log file. An archive being built gets the same, counted
/// in its own files: it is one item of the Write phase, and building it can take minutes. Every line ends with a memory
/// sample, and a phase that has gone 30 seconds without a line repeats its progress on the next report, so a long
/// stretch inside one 5% step still shows how memory grew.
/// </summary>
public sealed class InstallProgressLog(ILogger logger, string list, IMemorySampler memory, TimeProvider time)
{
    private const int StepPercent = 5;
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(30);

    private long _lastWrite = time.GetTimestamp();

    private InstallPhase? _phase;
    private int _step;
    private readonly Dictionary<int, int> _archiveSteps = [];

    /// <summary>Logs <paramref name="snapshot"/> when it starts a phase or crosses the next 5% of its phase.</summary>
    public void Report(ModlistInstallProgress.Snapshot snapshot)
    {
        ReportPhase(snapshot);
        ReportArchive(snapshot.Archive);
    }

    private void ReportArchive(ModlistInstallProgress.ArchiveBuild? archive)
    {
        if (archive is null) return;

        // Builds run side by side and a snapshot shows the one that reported, so each keeps its own last step.
        var started = !_archiveSteps.TryGetValue(archive.Number, out var last);
        var step = archive.TotalFiles <= 0 ? 0 : (int)(Math.Clamp((double)archive.DoneFiles / archive.TotalFiles, 0, 1) * 100) / StepPercent;
        if (!started && step <= last) return;

        _archiveSteps[archive.Number] = step;
        Write(archive.Describe(CultureInfo.InvariantCulture));
    }

    private void ReportPhase(ModlistInstallProgress.Snapshot snapshot)
    {
        var started = _phase != snapshot.Phase;
        if (started)
        {
            _phase = snapshot.Phase;
            _step = 0;
        }

        if (snapshot.TotalItems <= 0)
        {
            if (started) Write(PhaseName(snapshot.Phase));
            return;
        }

        var percent = (int)(Math.Clamp((double)snapshot.DoneItems / snapshot.TotalItems, 0, 1) * 100);
        var step = percent / StepPercent;
        if (!started && step <= _step && time.GetElapsedTime(_lastWrite) < HeartbeatEvery) return;

        _step = Math.Max(step, _step);
        Write(string.Create(CultureInfo.InvariantCulture, $"{PhaseName(snapshot.Phase)} {snapshot.DoneItems:N0} of {snapshot.TotalItems:N0} ({percent}%)"));
    }

    private void Write(string text)
    {
        _lastWrite = time.GetTimestamp();
        logger.LogInformation("Install of {List}: {Progress}; memory: {Memory}", list, text, memory.Sample().Describe());
    }

    private static string PhaseName(InstallPhase phase) => phase switch
    {
        InstallPhase.Check => "Checking the list",
        InstallPhase.Resolve => "Locating files in archives",
        InstallPhase.Write => "Writing files",
        InstallPhase.Order => "Ordering mods and plugins",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown install phase"),
    };
}

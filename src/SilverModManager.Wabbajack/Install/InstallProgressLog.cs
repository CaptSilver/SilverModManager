using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// Writes an install's progress to the log at Information: one line when a phase starts and one each time a phase
/// passes another 5%, so a long install can be followed in the log file.
/// </summary>
public sealed class InstallProgressLog(ILogger logger, string list)
{
    private const int StepPercent = 5;

    private InstallPhase? _phase;
    private int _step;

    /// <summary>Logs <paramref name="snapshot"/> when it starts a phase or crosses the next 5% of its phase.</summary>
    public void Report(ModlistInstallProgress.Snapshot snapshot)
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
        if (!started && step <= _step) return;

        _step = step;
        Write(string.Create(CultureInfo.InvariantCulture, $"{PhaseName(snapshot.Phase)} {snapshot.DoneItems:N0} of {snapshot.TotalItems:N0} ({percent}%)"));
    }

    private void Write(string text) => logger.LogInformation("Install of {List}: {Progress}", list, text);

    private static string PhaseName(InstallPhase phase) => phase switch
    {
        InstallPhase.Check => "Checking the list",
        InstallPhase.Resolve => "Locating files in archives",
        InstallPhase.Write => "Writing files",
        InstallPhase.Order => "Ordering mods and plugins",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown install phase"),
    };
}

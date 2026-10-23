using System.ComponentModel;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Platform.Proton.ToolRunner.Dependencies;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Selftest;

/// <summary>
/// Checks the shipped binary and the machine it runs on, without touching a game, a launcher or any real
/// state. The flock and signal checks start the binary again with its hidden <c>selftest-helper</c> commands,
/// so they test the executable the user actually has (an AppImage included) instead of the code in this process.
/// </summary>
/// <param name="executable">The running binary; replaced by <c>$APPIMAGE</c> when that is set.</param>
/// <param name="shim">The launcher script that Steam launch options point at.</param>
/// <param name="scratchRoot">Where the lock file goes, in a folder removed afterwards. The system temp directory when null.</param>
/// <param name="devFusePath">The FUSE device node.</param>
/// <param name="timeout">How long each wait on a helper may take.</param>
public sealed class SelftestRunner(
    AbsolutePath executable,
    LauncherShim shim,
    IEnvironmentReader environment,
    IProcessRunner processRunner,
    string? scratchRoot = null,
    string devFusePath = "/dev/fuse",
    TimeSpan? timeout = null)
{
    public const string ShimCheck = "Launcher shim";
    public const string LockCheck = "File lock";
    public const string SignalCheck = "SIGTERM handling";
    public const string NotifySendCheck = "notify-send";
    public const string FuseCheck = "/dev/fuse";
    public const string UmuCheck = "umu-run";
    public const string WineCheck = "wine";
    public const string FlatpakCheck = "flatpak";

    private const string NotifySend = "notify-send";
    private const int SigTermExitCode = 143;
    private const string HeldSeconds = "1";

    private TimeSpan Timeout => timeout ?? TimeSpan.FromSeconds(30);

    private string? AppImage => environment.Get("APPIMAGE") is { Length: > 0 } path ? path : null;

    // The AppImage runtime sits in front of the app, so it is the process whose signal handling matters.
    private string Target => AppImage ?? executable.ToString();

    public async Task<SelftestReport> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checks = new List<SelftestCheck>
        {
            await CheckShim(cancellationToken),
            await CheckLock(cancellationToken),
            await CheckSignal(cancellationToken),
            CheckNotifySend(),
            CheckFuse(),
            await Probe(UmuCheck, new UmuRunDependency(processRunner), cancellationToken),
            await Probe(WineCheck, new WineDependency(processRunner, "wine"), cancellationToken),
            await Probe(FlatpakCheck, new FlatpakDependency(processRunner), cancellationToken),
        };
        return new SelftestReport(checks);
    }

    private async Task<SelftestCheck> CheckShim(CancellationToken cancellationToken)
    {
        var path = shim.ShimPath.ToString();
        if (!File.Exists(path))
            return Required(ShimCheck, false, $"{path} does not exist. Start the app once to create it.");
        if (!shim.Targets(executable))
            return Required(ShimCheck, false, $"{path} does not exec {executable}, so Steam launch lines start another install. Start the app once to point it here.");

        try
        {
            using var helper = HelperProcess.Start(path, "selftest-helper", "ping");
            var line = await helper.ReadLineAsync(Timeout, cancellationToken);
            var exitCode = await helper.WaitForExitAsync(Timeout, cancellationToken);
            return line == "pong" && exitCode == 0
                ? Required(ShimCheck, true, $"{path} starts the app")
                : Required(ShimCheck, false, $"{path} answered '{line}' and exited with {exitCode?.ToString() ?? "no exit"}{await Stderr(helper)}; it probably points at a binary that has moved.");
        }
        catch (Win32Exception e)
        {
            return Required(ShimCheck, false, $"{path} could not be run: {e.Message}");
        }
    }

    private async Task<SelftestCheck> CheckLock(CancellationToken cancellationToken)
    {
        var folder = Directory.CreateDirectory(Path.Combine(ScratchRoot(), $"silvermod-selftest-{Guid.NewGuid():N}")).FullName;
        try
        {
            var lockPath = Path.Combine(folder, "selftest.lock");
            using var helper = HelperProcess.Start(Target, "selftest-helper", "hold-lock", lockPath, HeldSeconds);

            var line = await helper.ReadLineAsync(Timeout, cancellationToken);
            if (line != "locked")
                return Required(LockCheck, false, $"{Target} did not take the lock (said '{line}'){await Stderr(helper)}");

            using var probe = new InstallationLock(lockPath);
            if (!probe.Probe())
                return Required(LockCheck, false, "The lock was not held while the helper said it had it, so flock does not work across processes here.");

            var exitCode = await helper.WaitForExitAsync(Timeout, cancellationToken);
            if (exitCode != 0)
                return Required(LockCheck, false, $"The lock helper exited with {exitCode?.ToString() ?? "no exit"}{await Stderr(helper)}");

            return probe.Probe()
                ? Required(LockCheck, false, "The lock was still held after the helper exited.")
                : Required(LockCheck, true, "held while the helper ran, free after it exited");
        }
        catch (Win32Exception e)
        {
            return Required(LockCheck, false, $"{Target} could not be run: {e.Message}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private async Task<SelftestCheck> CheckSignal(CancellationToken cancellationToken)
    {
        var through = AppImage is null ? "" : " through the AppImage runtime";
        try
        {
            using var helper = HelperProcess.Start(Target, "selftest-helper", "wait-for-term");

            var line = await helper.ReadLineAsync(Timeout, cancellationToken);
            if (line != "ready")
                return Required(SignalCheck, false, $"{Target} did not get ready for a signal (said '{line}'){await Stderr(helper)}");

            if (!helper.Terminate())
                return Required(SignalCheck, false, $"The helper exited with {await helper.WaitForExitAsync(Timeout, cancellationToken)} before SIGTERM{through} was sent.");
            var exitCode = await helper.WaitForExitAsync(Timeout, cancellationToken);
            return exitCode == SigTermExitCode
                ? Required(SignalCheck, true, $"SIGTERM{through} reached the stop handler and ended the helper with {SigTermExitCode}")
                : Required(SignalCheck, false, $"After SIGTERM{through} the helper {(exitCode is { } code ? $"exited with {code}, not {SigTermExitCode}" : "was still running")}; the signal is not reaching the app.");
        }
        catch (Win32Exception e)
        {
            return Required(SignalCheck, false, $"{Target} could not be run: {e.Message}");
        }
    }

    // Looked up like a shell would, and never run: a notification would appear on the user's desktop.
    private SelftestCheck CheckNotifySend()
    {
        var search = environment.Get("PATH") ?? "";
        foreach (var directory in search.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, NotifySend);
            if (File.Exists(candidate) && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return Required(NotifySendCheck, true, candidate);
        }
        return Required(NotifySendCheck, false, "Not on PATH, so a launch that fails without a window can't tell the user why. It comes with libnotify.");
    }

    private SelftestCheck CheckFuse() => File.Exists(devFusePath)
        ? new SelftestCheck(FuseCheck, SelftestStatus.Passed, false, $"{devFusePath} is present")
        : new SelftestCheck(FuseCheck, SelftestStatus.Absent, false, $"{devFusePath} is missing, so the FUSE virtual file system and AppImages can't run.");

    private static async Task<SelftestCheck> Probe(string name, ProbedDependency dependency, CancellationToken cancellationToken)
    {
        try
        {
            var information = await dependency.QueryInstallationInformation(cancellationToken);
            if (!information.HasValue) return new SelftestCheck(name, SelftestStatus.Absent, false, "not installed or not on PATH");

            var version = information.Value.RawVersion;
            return new SelftestCheck(name, SelftestStatus.Passed, false, version.HasValue ? version.Value : "installed");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return new SelftestCheck(name, SelftestStatus.Absent, false, $"could not be run: {e.Message}");
        }
    }

    private string ScratchRoot()
    {
        var root = scratchRoot ?? Path.GetTempPath();
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<string> Stderr(HelperProcess helper)
    {
        var error = await helper.ErrorAsync();
        return error.Length == 0 ? "" : $" ({error})";
    }

    private static SelftestCheck Required(string name, bool passed, string detail)
        => new(name, passed ? SelftestStatus.Passed : SelftestStatus.Failed, true, detail);
}

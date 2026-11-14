using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// What to run: the program with its arguments, where, and on top of which environment.
/// </summary>
/// <param name="Argv">The program followed by its arguments.</param>
/// <param name="WorkingDirectory">The directory to start in, or null to inherit ours.</param>
/// <param name="Environment">Variables set on top of the inherited environment.</param>
/// <param name="FilesystemRoots">
/// Host folders the Steam container must be able to write to, merged into
/// <c>PRESSURE_VESSEL_FILESYSTEMS_RW</c>. Empty with the copy backend, which needs nothing mounted.
/// </param>
/// <param name="WineDllOverrides">
/// DLL overrides the game needs, merged into the inherited <c>WINEDLLOVERRIDES</c> so the user's own entries
/// survive. Null or empty leaves the variable as it came.
/// </param>
/// <param name="DefaultEnvironment">
/// Variables set only where neither the inherited environment nor <paramref name="Environment"/> already has them,
/// so a value the user chose in the launch options is not overruled.
/// </param>
/// <param name="BaseEnvironment">
/// The environment to inherit instead of this process's own, for a launch that runs here on behalf of another
/// process (<c>run</c> handing Steam's variables to the window). Null inherits ours.
/// </param>
public sealed record GameProcessSpec(
    IReadOnlyList<string> Argv,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    IReadOnlyList<string>? FilesystemRoots = null,
    IReadOnlyList<WineDllOverride>? WineDllOverrides = null,
    IReadOnlyDictionary<string, string>? BaseEnvironment = null,
    IReadOnlyList<KeyValuePair<string, string>>? DefaultEnvironment = null);

/// <summary>
/// How a <see cref="GameProcess"/> ended.
/// </summary>
/// <param name="ExitCode">The child's exit code, or 128 plus the signal number when a signal ended it.</param>
/// <param name="KillFailed">A process of the game that survived SIGKILL, or null when the tree is gone.</param>
public sealed record GameProcessResult(int ExitCode, TrackedProcess? KillFailed);

/// <summary>
/// Runs the launch command as a direct child of this process, in our session and process group, so the
/// terminal's Ctrl-C and a closing session reach it without help. Steam's reaper and Proton need to be
/// children of whoever started them, which rules out <c>setsid</c> or a detached spawn.
/// </summary>
public sealed partial class GameProcess : IGameProcess
{
    /// <summary>
    /// How long a process gets between each step of shutting down: to exit by itself, to act on SIGTERM.
    /// </summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(5);

    private const int SigHup = 1;
    private const int SigKill = 9;
    private const int SigTerm = 15;

    private readonly Process _process;
    private readonly IRunningGameDetector? _detector;
    private readonly GameInstallation? _installation;
    private readonly TimeSpan _grace;
    private readonly TimeSpan _settle;
    private readonly StopRequest _stop = new();
    private readonly bool _forwardTermination;
    private readonly bool _leaveSignalsAlone;
    private readonly IReadOnlySet<int>? _protectedPids;
    private readonly ILogger? _logger;

    private GameProcess(Process process, IRunningGameDetector? detector, GameInstallation? installation, TimeSpan grace, TimeSpan settle, bool forwardTermination, bool leaveSignalsAlone, IReadOnlySet<int>? protectedPids, ILogger? logger)
    {
        _logger = logger;
        _protectedPids = protectedPids;
        _forwardTermination = forwardTermination;
        _leaveSignalsAlone = leaveSignalsAlone;
        _process = process;
        _detector = detector;
        _installation = installation;
        _grace = grace;
        _settle = settle;
    }

    /// <summary>The child's process id.</summary>
    public int Pid => _process.Id;

    /// <summary>
    /// Starts the command. After the child exits, any process the tracker still attributes to the
    /// installation is shut down.
    /// </summary>
    /// <param name="baseEnvironment">The environment to inherit; defaults to ours.</param>
    /// <param name="grace">Overrides <see cref="DefaultGrace"/>.</param>
    /// <param name="settle">
    /// How long the game's processes must stay gone before <see cref="WaitAsync"/> ends. Defaults to
    /// <see cref="SettlePeriods.Steam"/>, because the headless command runs under Steam's reaper.
    /// </param>
    /// <param name="forwardTermination">
    /// Whether <see cref="WaitAsync"/> passes SIGTERM and SIGHUP on to the child. Turn it off when the owner
    /// already stops the child on those signals, or the child would get each of them twice.
    /// </param>
    /// <param name="protectedPids">Processes the wait and the sweep ignore and never signal, with their ancestors.</param>
    /// <param name="leaveSignalsAlone">
    /// Whether <see cref="WaitAsync"/> registers no signal handling at all. For the window, whose own shutdown must
    /// not be swallowed while a game runs; it stops the child through the launch session instead.
    /// </param>
    public static GameProcess Start(
        GameProcessSpec spec,
        IRunningGameDetector detector,
        GameInstallation installation,
        IReadOnlyDictionary<string, string>? baseEnvironment = null,
        TimeSpan? grace = null,
        bool forwardTermination = true,
        bool leaveSignalsAlone = false,
        ILogger? logger = null,
        Func<string, bool>? isInstalledSteamAppId = null,
        IReadOnlySet<int>? protectedPids = null,
        TimeSpan? settle = null)
    {
        return Spawn(spec, detector, installation, protectedPids, baseEnvironment, grace, settle, forwardTermination, leaveSignalsAlone, logger, isInstalledSteamAppId);
    }

    /// <summary>
    /// Starts the command for a launch the GUI already owns. Only the spec's Wine DLL overrides are merged
    /// into the environment (the GUI's launch mounted the filesystem roots already) and nothing is cleaned up
    /// afterwards: the GUI's session ends the game.
    /// </summary>
    public static GameProcess PassThrough(GameProcessSpec spec, IReadOnlyDictionary<string, string>? baseEnvironment = null)
    {
        return Spawn(spec, detector: null, installation: null, protectedPids: null, baseEnvironment, grace: null, settle: null, forwardTermination: true, leaveSignalsAlone: false, logger: null, isInstalledSteamAppId: null);
    }

    private static GameProcess Spawn(
        GameProcessSpec spec,
        IRunningGameDetector? detector,
        GameInstallation? installation,
        IReadOnlySet<int>? protectedPids,
        IReadOnlyDictionary<string, string>? baseEnvironment,
        TimeSpan? grace,
        TimeSpan? settle,
        bool forwardTermination,
        bool leaveSignalsAlone,
        ILogger? logger,
        Func<string, bool>? isInstalledSteamAppId)
    {
        if (spec.Argv.Count == 0) throw new ArgumentException("The command has no program.", nameof(spec));

        var startInfo = new ProcessStartInfo(spec.Argv[0]) { UseShellExecute = false };
        foreach (var arg in spec.Argv.Skip(1)) startInfo.ArgumentList.Add(arg);
        if (spec.WorkingDirectory is not null) startInfo.WorkingDirectory = spec.WorkingDirectory;

        var environment = BuildEnvironment(baseEnvironment ?? spec.BaseEnvironment ?? CurrentEnvironment(), spec, isInstalledSteamAppId);
        startInfo.Environment.Clear();
        foreach (var (key, value) in environment) startInfo.Environment[key] = value;

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {spec.Argv[0]}");
        return new GameProcess(process, detector, installation, grace ?? DefaultGrace, settle ?? SettlePeriods.Steam, forwardTermination, leaveSignalsAlone, protectedPids, logger);
    }

    private static Dictionary<string, string> CurrentEnvironment()
    {
        var result = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            result[(string)entry.Key] = (string?)entry.Value ?? "";
        return result;
    }

    // The variables our secret store reads credentials from (the overlay's aliases and its own names). A game, and
    // everything a game's launcher spawns, could read them and send them anywhere.
    private static readonly string[] SecretVariables = ["NEXUS_API_KEY", "GITHUB_TOKEN", "STEAM_USER", "STEAM_PASS", "SILVERMM_SECRETS_FILE"];
    private const string SecretVariablePrefix = "SILVERMM_SECRET_";

    /// <summary>
    /// The environment the child gets. When we run from an AppImage its variables point into the mounted
    /// image, which would leak into Steam, Proton and the game and break their own library lookups.
    /// <c>SteamAppId</c> and <c>SteamGameId</c> are kept, unless they belong to the app itself running as a Steam
    /// shortcut (see <paramref name="isInstalledSteamAppId"/>).
    /// </summary>
    /// <remarks>
    /// Our credential variables are removed too, so a key given to <c>run</c> through its environment does not
    /// reach the game. They go before the spec's own environment is applied: a launch option that sets one of
    /// them on purpose still gets it.
    /// </remarks>
    /// <param name="inherited">The environment of the process starting the game.</param>
    /// <param name="spec">What to start.</param>
    /// <param name="isInstalledSteamAppId">
    /// Whether an app id names a Steam game installed here. An inherited <c>SteamAppId</c> that names none is the
    /// shortcut's own id, and it, <c>SteamGameId</c>, <c>SteamOverlayGameId</c> and Steam's overlay preload would make
    /// Steam treat the game as part of the shortcut's session: ending the session, the overlay and the playtime
    /// would be the shortcut's. Null never strips, which is what a <c>run</c> process started by a game's own launch
    /// option needs.
    /// </param>
    internal static Dictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string> inherited, GameProcessSpec spec, Func<string, bool>? isInstalledSteamAppId = null)
    {
        var result = new Dictionary<string, string>(inherited);
        var stripShortcutIdentity = isInstalledSteamAppId is not null
            && result.TryGetValue("SteamAppId", out var steamAppId)
            && !isInstalledSteamAppId(steamAppId);
        if (stripShortcutIdentity)
        {
            foreach (var name in SteamShortcutVariables) result.Remove(name);
            DropOverlayPreload(result);
        }
        result.TryGetValue("APPDIR", out var appDir);

        foreach (var name in new[] { "APPIMAGE", "APPDIR", "OWD" }) result.Remove(name);
        foreach (var name in new[] { "LD_LIBRARY_PATH", "LD_PRELOAD" })
        {
            if (!result.TryGetValue(name, out var value) || string.IsNullOrEmpty(appDir)) continue;

            var kept = value.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Where(entry => !IsUnder(entry, appDir))
                .ToArray();
            if (kept.Length == 0) result.Remove(name);
            else result[name] = string.Join(':', kept);
        }

        foreach (var name in SecretVariables) result.Remove(name);
        foreach (var name in result.Keys.Where(name => name.StartsWith(SecretVariablePrefix, StringComparison.Ordinal)).ToArray()) result.Remove(name);

        if (spec.Environment is not null)
        {
            foreach (var (key, value) in spec.Environment) result[key] = value;
        }

        if (spec.DefaultEnvironment is not null)
        {
            foreach (var (key, value) in spec.DefaultEnvironment) result.TryAdd(key, value);
        }

        if (spec.WineDllOverrides is { Count: > 0 } overrides)
        {
            result.TryGetValue(WineParser.WineDllOverridesEnvironmentVariableName, out var existing);
            result[WineParser.WineDllOverridesEnvironmentVariableName] = WineParser.MergeDllOverrides(existing ?? "", overrides);
        }

        if (spec.FilesystemRoots is { } roots) PressureVesselFilesystems.Merge(result, roots);

        return result;
    }

    internal static readonly string[] SteamShortcutVariables = ["SteamAppId", "SteamGameId", "SteamOverlayGameId"];

    private static void DropOverlayPreload(Dictionary<string, string> environment)
    {
        if (!environment.TryGetValue("LD_PRELOAD", out var value)) return;

        var kept = value.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => !Path.GetFileName(entry).Contains("gameoverlayrenderer", StringComparison.Ordinal))
            .ToArray();
        if (kept.Length == 0) environment.Remove("LD_PRELOAD");
        else environment["LD_PRELOAD"] = string.Join(':', kept);
    }

    private static bool IsUnder(string path, string folder)
    {
        var root = folder.TrimEnd('/');
        return path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Waits for the child to exit, forwarding termination signals sent to us while it runs, then shuts
    /// down whatever of the game is left.
    /// </summary>
    public async Task<GameProcessResult> WaitAsync(CancellationToken cancellationToken = default)
    {
        // SIGINT and SIGQUIT come from the terminal and reach the child already, because it shares our
        // process group; forwarding them as well would deliver Ctrl-C twice. They only need to stop
        // killing us. SIGTERM and SIGHUP are sent to one pid, so those are passed on.
        // An owner that stops the child itself leaves these two out, or the child would get each signal twice.
        using var term = _forwardTermination && !_leaveSignalsAlone
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; _stop.Set(); Signal(SigTerm); })
            : null;
        using var hup = _forwardTermination && !_leaveSignalsAlone
            ? PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => { context.Cancel = true; _stop.Set(); Signal(SigHup); })
            : null;
        using var interrupt = _leaveSignalsAlone ? null : PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        using var quit = _leaveSignalsAlone ? null : PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context => context.Cancel = true);

        await _process.WaitForExitAsync(cancellationToken);
        var exitCode = _process.ExitCode;

        TrackedProcess? survivor = null;
        if (_detector is { } detector && _installation is { } installation)
        {
            // The child is often only the launcher: it exits while the game is still starting, or hands over to a
            // new process. Sweeping on the first empty scan would kill a game that is alive a moment later, so
            // the sweep waits for the game to stay gone, unless a stop was asked for. A stop that the game
            // ignores must not leave the wait polling for ever.
            var left = await detector.WaitUntilGone(installation, new GoneWait(_settle, _protectedPids, _stop), cancellationToken);
            if (left.Any) survivor = await ShutDownStragglersAsync(detector, installation, cancellationToken);
        }
        return new GameProcessResult(exitCode, survivor);
    }

    /// <summary>
    /// Asks the child to stop with SIGTERM and kills it with SIGKILL when it is still there after the grace, and
    /// makes <see cref="WaitAsync"/> sweep what is left of the game without waiting for it to leave.
    /// </summary>
    public void Terminate()
    {
        // Also when the child is already gone: the game's other processes are then swept without waiting for them.
        _stop.Set();
        if (_process.HasExited) return;

        Signal(SigTerm);
        if (_process.WaitForExit(_grace)) return;

        Signal(SigKill);
        _process.WaitForExit();
    }

    private void Signal(int signal)
    {
        try
        {
            if (!_process.HasExited) kill(_process.Id, signal);
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the call; there is nothing left to signal.
        }
    }

    /// <summary>
    /// The child is gone but Wine servers, the reaper or the game itself can outlive it. They get the grace
    /// to leave on their own, then SIGTERM, then SIGKILL.
    /// </summary>
    private Task<TrackedProcess?> ShutDownStragglersAsync(IRunningGameDetector detector, GameInstallation installation, CancellationToken cancellationToken)
        => StragglerShutdown.RunAsync(detector, installation, _grace, cancellationToken, _protectedPids, logger: _logger);

    /// <inheritdoc />
    public void Dispose() => _process.Dispose();

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}

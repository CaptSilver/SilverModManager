using System.Diagnostics;
using System.Runtime.InteropServices;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Launch;

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
public sealed record GameProcessSpec(
    IReadOnlyList<string> Argv,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    IReadOnlyList<string>? FilesystemRoots = null,
    IReadOnlyList<WineDllOverride>? WineDllOverrides = null);

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
    private readonly IProcessTracker? _tracker;
    private readonly GameInstallation? _installation;
    private readonly TimeSpan _grace;
    private readonly bool _forwardTermination;

    private GameProcess(Process process, IProcessTracker? tracker, GameInstallation? installation, TimeSpan grace, bool forwardTermination)
    {
        _forwardTermination = forwardTermination;
        _process = process;
        _tracker = tracker;
        _installation = installation;
        _grace = grace;
    }

    /// <summary>The child's process id.</summary>
    public int Pid => _process.Id;

    /// <summary>
    /// Starts the command. After the child exits, any process the tracker still attributes to the
    /// installation is shut down.
    /// </summary>
    /// <param name="baseEnvironment">The environment to inherit; defaults to ours.</param>
    /// <param name="grace">Overrides <see cref="DefaultGrace"/>.</param>
    /// <param name="forwardTermination">
    /// Whether <see cref="WaitAsync"/> passes SIGTERM and SIGHUP on to the child. Turn it off when the owner
    /// already stops the child on those signals, or the child would get each of them twice.
    /// </param>
    public static GameProcess Start(
        GameProcessSpec spec,
        IProcessTracker tracker,
        GameInstallation installation,
        IReadOnlyDictionary<string, string>? baseEnvironment = null,
        TimeSpan? grace = null,
        bool forwardTermination = true)
    {
        return Spawn(spec, tracker, installation, baseEnvironment, grace, forwardTermination);
    }

    /// <summary>
    /// Starts the command for a launch the GUI already owns. Only the spec's Wine DLL overrides are merged
    /// into the environment (the GUI's launch mounted the filesystem roots already) and nothing is cleaned up
    /// afterwards: the GUI's session ends the game.
    /// </summary>
    public static GameProcess PassThrough(GameProcessSpec spec, IReadOnlyDictionary<string, string>? baseEnvironment = null)
    {
        return Spawn(spec, tracker: null, installation: null, baseEnvironment, grace: null, forwardTermination: true);
    }

    private static GameProcess Spawn(
        GameProcessSpec spec,
        IProcessTracker? tracker,
        GameInstallation? installation,
        IReadOnlyDictionary<string, string>? baseEnvironment,
        TimeSpan? grace,
        bool forwardTermination)
    {
        if (spec.Argv.Count == 0) throw new ArgumentException("The command has no program.", nameof(spec));

        var startInfo = new ProcessStartInfo(spec.Argv[0]) { UseShellExecute = false };
        foreach (var arg in spec.Argv.Skip(1)) startInfo.ArgumentList.Add(arg);
        if (spec.WorkingDirectory is not null) startInfo.WorkingDirectory = spec.WorkingDirectory;

        var environment = BuildEnvironment(baseEnvironment ?? CurrentEnvironment(), spec);
        startInfo.Environment.Clear();
        foreach (var (key, value) in environment) startInfo.Environment[key] = value;

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {spec.Argv[0]}");
        return new GameProcess(process, tracker, installation, grace ?? DefaultGrace, forwardTermination);
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
    /// <c>SteamAppId</c> and <c>SteamGameId</c> are deliberately not touched.
    /// </summary>
    /// <remarks>
    /// Our credential variables are removed too, so a key given to <c>run</c> through its environment does not
    /// reach the game. They go before the spec's own environment is applied: a launch option that sets one of
    /// them on purpose still gets it.
    /// </remarks>
    internal static Dictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string> inherited, GameProcessSpec spec)
    {
        var result = new Dictionary<string, string>(inherited);
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

        if (spec.WineDllOverrides is { Count: > 0 } overrides)
        {
            result.TryGetValue(WineParser.WineDllOverridesEnvironmentVariableName, out var existing);
            result[WineParser.WineDllOverridesEnvironmentVariableName] = WineParser.MergeDllOverrides(existing ?? "", overrides);
        }

        if (spec.FilesystemRoots is { } roots) PressureVesselFilesystems.Merge(result, roots);

        return result;
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
        using var term = _forwardTermination
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; Signal(SigTerm); })
            : null;
        using var hup = _forwardTermination
            ? PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => { context.Cancel = true; Signal(SigHup); })
            : null;
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context => context.Cancel = true);

        await _process.WaitForExitAsync(cancellationToken);
        var exitCode = _process.ExitCode;

        var survivor = _tracker is null || _installation is null ? null : await ShutDownStragglersAsync(cancellationToken);
        return new GameProcessResult(exitCode, survivor);
    }

    /// <summary>
    /// Asks the child to stop with SIGTERM and kills it with SIGKILL when it is still there after the grace.
    /// </summary>
    public void Terminate()
    {
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
    private Task<TrackedProcess?> ShutDownStragglersAsync(CancellationToken cancellationToken)
        => StragglerShutdown.RunAsync(_tracker!, _installation!, _grace, cancellationToken);

    /// <summary>
    /// Whether a pid can be signalled as one process. <c>kill(2)</c> reads 0 as our process group and -1 as
    /// every process we may signal, and the tracker can match us too when we were started with the game's
    /// <c>WINEPREFIX</c> in our environment.
    /// </summary>
    public static bool IsSafeToSignal(int pid) => pid > 1 && pid != Environment.ProcessId;

    /// <inheritdoc />
    public void Dispose() => _process.Dispose();

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}

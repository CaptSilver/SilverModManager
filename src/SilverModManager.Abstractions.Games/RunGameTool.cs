using System.Diagnostics;
using CliWrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using R3;
using BehaviorSubject = System.Reactive.Subjects.BehaviorSubject<bool>;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// Marker interface for RunGameTool
/// </summary>
public interface IRunGameTool : ITool
{
    /// <summary>
    /// Resolves what this tool would start for the loadout, without starting it.
    /// </summary>
    ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken);

    /// <summary>
    /// True while a launch has handed the game to Steam and Steam has not started it yet, which is the phase
    /// the user can still back out of. Emits the current value on subscribe and only changes after that.
    /// </summary>
    IObservable<bool> WaitingForSteam { get; }
}

/// <summary>
/// A tool that launches the game, using first found installation.
/// </summary>
/// <typeparam name="T"></typeparam>
public class RunGameTool<T> : IRunGameTool
    where T : IGame
{
    private readonly ILogger<RunGameTool<T>> _logger;
    private readonly T _game;
    private readonly IProcessRunner _processRunner;
    private readonly IToolRunner _toolRunner;
    private readonly IPrefixResolver _prefixResolver;
    private readonly IOSInterop _osInterop;
    private readonly IProcessTracker _processTracker;
    private readonly IGameLaunchSession _launchSessions;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Whether this tool should be started through the shell instead of directly.
    /// This allows tools to start their own console, allowing users to interact with it.
    /// </summary>
    protected virtual bool UseShell { get; set; } = false;

    /// <summary>How long Steam, then its reaper for the game, get to show up after the URI is opened.</summary>
    protected virtual TimeSpan StartTimeout => TimeSpan.FromMinutes(5);

    /// <summary>How often the process table is looked at while waiting.</summary>
    protected virtual TimeSpan PollInterval => TimeSpan.FromMilliseconds(300);

    private const int ReaperGoneAfterMisses = 3;

    private readonly BehaviorSubject _waitingForSteam = new(false);
    private int _launchesWaitingForSteam;

    /// <inheritdoc />
    public IObservable<bool> WaitingForSteam => _waitingForSteam;

    /// <summary>
    /// Constructor
    /// </summary>
    public RunGameTool(IServiceProvider serviceProvider, T game)
    {
        _game = game;
        _serviceProvider = serviceProvider;
        _logger = serviceProvider.GetRequiredService<ILogger<RunGameTool<T>>>();
        _processRunner = serviceProvider.GetRequiredService<IProcessRunner>();
        _toolRunner = serviceProvider.GetRequiredService<IToolRunner>();
        _prefixResolver = serviceProvider.GetRequiredService<IPrefixResolver>();
        _osInterop = serviceProvider.GetRequiredService<IOSInterop>();
        _processTracker = serviceProvider.GetRequiredService<IProcessTracker>();
        _launchSessions = serviceProvider.GetRequiredService<IGameLaunchSession>();
    }

    /// <inheritdoc />
    public IEnumerable<GameId> GameIds => [_game.GameId];

    /// <inheritdoc />
    public string Name => $"Run {_game.DisplayName}";

    /// <inheritdoc />
    public virtual async ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        return new LaunchSpec(await GetGamePath(loadout), []);
    }

    /// <summary/>
    public virtual async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken, string[]? commandLineArgs)
    {
        _logger.LogInformation("Starting {Name}", Name);

        var spec = await GetLaunchSpec(loadout, cancellationToken);
        var program = spec.Exe;
        commandLineArgs = [..commandLineArgs ?? [], ..spec.ExtraArgs];
        var primaryFile = loadout.InstallationInstance.Locations.ToAbsolutePath(_game.GetPrimaryFile(loadout.InstallationInstance));

        if (program.Equals(primaryFile))
        {
            var locatorResult = loadout.InstallationInstance.LocatorResult;
            if (locatorResult.Store == GameStore.Steam)
            {
                await RunThroughSteam(loadout, locatorResult.StoreIdentifier, spec, cancellationToken);
                return;
            }

            if (locatorResult.Store == GameStore.GOG)
            {
                await RunThroughHeroic("gog", locatorResult.StoreIdentifier, cancellationToken, commandLineArgs);
                return;
            }
        }

        // A script extender or launcher of a Windows build is a Windows program, and Linux cannot start it
        // directly. Asked before the session so the loadout is not applied for a program that cannot run.
        var installation = loadout.InstallationInstance;
        var viaToolRunner = LaunchPlatform.IsWindowsProgram(_prefixResolver.Resolve(installation), installation.LocatorResult);
        if (viaToolRunner && !_toolRunner.CanRun(installation, out var cannotRunReason))
            throw new InvalidOperationException($"Cannot run {program.FileName} for {_game.DisplayName}: {cannotRunReason}");

        // The launcher or loader is started by us, so the session's lock covers it and everything it spawns.
        using var launch = _launchSessions.BeginSession(loadout);
        await launch.RunAsync(token => RunProgram(installation, viaToolRunner, program, primaryFile, commandLineArgs, token), cancellationToken);
    }

    private async Task RunProgram(GameInstallation installation, bool viaToolRunner, AbsolutePath program, AbsolutePath primaryFile, string[] commandLineArgs, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>
        {
            program.FileName,
            program.GetFileNameWithoutExtension(),
            primaryFile.FileName,
            primaryFile.GetFileNameWithoutExtension(),
        };

        // In the case of a preloader, we need to wait for the actual game file to exit
        // before we completely exit this routine. So get a list of all the processes with a give
        // name at the start, after the preloader finishes find any other processes with the same set of
        // names, and then we wait for those to exit.

        // In the case of something like Skyrim this means we will start with loading skse64_loader.exe then
        // notice that SkyrimSE.exe is running and wait for that to exit.

        var existing = FindMatchingProcesses(names).Select(p => p.Pid).ToHashSet();
            
        if (viaToolRunner)
        {
            // The shell would hand a Windows program to the host, which cannot open it, so this wins over UseShell.
            _logger.LogInformation("Running {Program} through the tool runner", program);
            var result = await _toolRunner.RunWindowsTool(installation, program, commandLineArgs, null, logOutput: true, cancellationToken);
            if (result.ExitCode != 0)
                _logger.LogWarning("Application closed with a non-zero exit code ({ExitCode}): {Application}", result.ExitCode, program);
        }
        else if (UseShell)
        {
            _logger.LogInformation("Running {Program} through shell", program);
            await RunWithShell(cancellationToken, program);
        }
        else
        {
            _ = await RunCommand(cancellationToken, commandLineArgs, program);
        }


        var maxProcessCount = 0;
        _logger.LogInformation("Waiting for processes to exit");
        while (true)
        {
            var newProcesses = CheckForNewProcesses();
            if (newProcesses.Count == 0)
                break;
            maxProcessCount = Math.Max(maxProcessCount, newProcesses.Count);
            await Task.Delay(2000, cancellationToken);
        }
        _logger.LogInformation("All {Count} processes have exited", maxProcessCount);

        _logger.LogInformation("Finished running {Program}", program);

        HashSet<TrackedProcess> CheckForNewProcesses()
        {
            // Check if the process has spawned any new processes that we need to wait for (e.g. Launcher -> Game)
            var hashSet = FindMatchingProcesses(names)
                .Where(p => !existing.Contains(p.Pid))
                .ToHashSet();
            return hashSet;
        }
    }

    private async Task<CommandResult> RunCommand(CancellationToken cancellationToken, string[] arguments, AbsolutePath program)
    {
        var command = new Command(program.ToString())
            .WithArguments(arguments)
            .WithWorkingDirectory(program.Parent.ToString());

        var result = await _processRunner.RunAsync(command, cancellationToken: cancellationToken);
        return result;
    }

    private async Task<Process> RunWithShell(CancellationToken cancellationToken, AbsolutePath program)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = program.ToString(),
                WorkingDirectory = program.Parent.ToString(),
                UseShellExecute = true,
                CreateNoWindow = false,
            },
            EnableRaisingEvents = true,
        };
        
        try
        {
            await _processRunner.RunAsync(process, cancellationToken);
        }
        catch (Exception e)
        {
            if (!cancellationToken.IsCancellationRequested)
                _logger.LogError(e, "While Running {Filename}", program);
        }
        
        if (process.ExitCode != 0)
            _logger.LogWarning("Application closed with a non-zero exit code ({ExitCode}): {Application}", process.ExitCode, program);
        return process;
    }

    private enum SteamRunOutcome
    {
        /// <summary>The game's reaper started and has exited.</summary>
        Exited,

        /// <summary>Steam, or the reaper for the game, did not show up in time.</summary>
        TimedOut,

        /// <summary>The caller stopped waiting.</summary>
        Cancelled,
    }

    private async Task RunThroughSteam(Loadout.ReadOnly loadout, string storeId, LaunchSpec spec, CancellationToken cancellationToken)
    {
        if (!uint.TryParse(storeId, out var appId))
            throw new InvalidOperationException($"'{storeId}' is not a Steam app id");

        // Never put arguments in this URL: Steam stops to ask the user to confirm them. The exe substitution and
        // extra arguments go into the launch session instead, and the app's Steam launch option applies them.
        // https://developer.valvesoftware.com/wiki/Steam_browser_protocol
        var steamUrl = $"steam://run/{storeId}";

        var outcome = SteamRunOutcome.Cancelled;
        using (var launch = _launchSessions.BeginSession(loadout, spec))
        {
            // The URI opens only once the session has the lock and the loadout is applied, so Steam never
            // starts the game against a half-applied folder.
            await launch.RunAsync(async token =>
            {
                _osInterop.OpenUri(new Uri(steamUrl));
                outcome = await WaitForSteamGame(launch, appId, token);
            }, cancellationToken);
        }

        // Said after the session has finished, so the user is not told to retry while the lock is still held.
        if (outcome == SteamRunOutcome.TimedOut)
        {
            _serviceProvider.GetRequiredService<IDesktopNotifier>().Notify(
                ApplicationConstants.ProductName,
                $"Steam did not start {_game.DisplayName} within {DescribeTimeout(StartTimeout)}. Make sure Steam is running and signed in, then press Play again.");
        }
    }

    /// <summary>
    /// The timeout as the user reads it: whole minutes when it is a whole number of them, seconds otherwise.
    /// </summary>
    internal static string DescribeTimeout(TimeSpan timeout)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        if (seconds % 60 == 0)
        {
            var minutes = seconds / 60;
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }
        return seconds == 1 ? "1 second" : $"{seconds} seconds";
    }

    private void SetWaitingForSteam(int delta)
    {
        // Two launches can wait at once (two installations of one game), so this counts them instead of
        // flipping a flag the first to finish would clear for the other.
        lock (_waitingForSteam)
        {
            _launchesWaitingForSteam += delta;
            if (_waitingForSteam.Value != _launchesWaitingForSteam > 0) _waitingForSteam.OnNext(_launchesWaitingForSteam > 0);
        }
    }

    private async Task<SteamRunOutcome> WaitForSteamGame(IGameLaunch launch, uint appId, CancellationToken cancellationToken)
    {
        SetWaitingForSteam(+1);
        var waiting = true;
        try
        {
            return await WaitForSteamGame(launch, appId, cancellationToken, () =>
            {
                if (!waiting) return;
                waiting = false;
                SetWaitingForSteam(-1);
            });
        }
        finally
        {
            if (waiting) SetWaitingForSteam(-1);
        }
    }

    private async Task<SteamRunOutcome> WaitForSteamGame(IGameLaunch launch, uint appId, CancellationToken cancellationToken, Action gameStarted)
    {
        if (await PollUntil(_processTracker.IsSteamRunning, launch, cancellationToken) is { } steamFailure) return steamFailure;

        // NOTE(erri120): Reaper is a custom tool for cleaning up child processes
        // See https://github.com/sonic2kk/steamtinkerlaunch/wiki/Steam-Reaper for details.
        // Only the reaper Steam started for this app counts: another game's reaper may be running already.
        TrackedProcess? reaper = null;
        if (await PollUntil(() => (reaper = _processTracker.FindReaperForApp(appId)) is not null, launch, cancellationToken) is { } reaperFailure)
            return reaperFailure;

        gameStarted();
        _logger.LogDebug("Waiting for reaper {Pid} of app {AppId} to exit", reaper!.Pid, appId);
        var misses = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            // A /proc entry that can't be read for a moment drops out of one scan. Taking that for the exit would
            // shut the game down while it runs, so the reaper has to be missing from several scans in a row.
            misses = _processTracker.FindReaperForApp(appId)?.Pid == reaper.Pid ? 0 : misses + 1;
            if (misses >= ReaperGoneAfterMisses) return SteamRunOutcome.Exited;
            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return SteamRunOutcome.Cancelled;
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, returning null, or returns why it stopped. A timeout is
    /// not final while a Steam wrapper has recorded itself in the launch's state file: that means the game is on
    /// its way and the process table has just not caught up.
    /// </summary>
    private async Task<SteamRunOutcome?> PollUntil(Func<bool> condition, IGameLaunch launch, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            if (condition()) return null;

            if (started.Elapsed >= StartTimeout)
            {
                // Read the file afresh every time: the wrapper writes it from another process.
                if (!launch.WrapperActive)
                {
                    _logger.LogWarning("Steam did not start the game within `{Timeout:g}`", StartTimeout);
                    return SteamRunOutcome.TimedOut;
                }
                started.Restart();
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return SteamRunOutcome.Cancelled;
    }

    private async Task RunThroughHeroic(string type, string productId, CancellationToken cancellationToken, string[] commandLineArgs)
    {
        // TODO: track process
        if (commandLineArgs.Length > 0)
            _logger.LogError("Heroic does not currently support command line arguments: https://github.com/Nexus-Mods/NexusMods.App/issues/2264 . " +
                             $"Args {string.Join(',', commandLineArgs)} were specified but will be ignored.");

        _osInterop.OpenUri(new Uri($"heroic://launch/{type}/{productId}"));
    }

    private HashSet<TrackedProcess> FindMatchingProcesses(HashSet<string> names)
    {
        return names.SelectMany(_processTracker.FindByName).ToHashSet();
    }

    /// <summary>
    /// Returns the path to the main executable file for the game.
    /// </summary>
    /// <param name="loadout"></param>
    /// <param name="applyPlan"></param>
    /// <returns></returns>
    protected virtual ValueTask<AbsolutePath> GetGamePath(Loadout.ReadOnly loadout)
    {
        var primaryFile = loadout.InstallationInstance.Locations.ToAbsolutePath(_game.GetPrimaryFile(loadout.InstallationInstance));
        return new ValueTask<AbsolutePath>(primaryFile);
    }

    /// <inheritdoc />
    public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
    {
        return monitor.Begin<ITool, Unit>(this, async _ =>
        {
            await Execute(loadout, cancellationToken, []);
            return Unit.Default;
        });
    }
}

internal static class LaunchPlatform
{
    /// <summary>
    /// Whether a program the tool starts itself needs a Wine prefix. The resolver also gives up on games it
    /// knows nothing about, such as a manually added native build, so a failed resolution only counts as
    /// Windows when the locator recorded a Windows platform or a compatibility layer for the game.
    /// </summary>
    public static bool IsWindowsProgram(PrefixResolution resolution, GameLocatorResult locatorResult)
    {
        if (resolution.IsNative) return false;
        if (resolution.IsResolved) return true;
        return locatorResult.TargetOS.IsWindows || locatorResult.Compatibility is not null;
    }
}

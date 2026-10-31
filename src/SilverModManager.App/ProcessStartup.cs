using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.SingleProcess;
using SilverModManager.SingleProcess.Exceptions;

namespace SilverModManager.App;

/// <summary>
/// The parts of process startup that need a real host, a real sync file or a window.
/// <see cref="Program"/> supplies the real ones, tests supply fakes.
/// </summary>
public sealed record ProcessStartupSteps
{
    public required Func<StartupMode, IHost> BuildHost { get; init; }
    public required Func<IServiceProvider, (Process? Process, MainKind Kind, int Port)> ReadSyncInfo { get; init; }
    public required Func<IServiceProvider, int, Task<bool>> CanConnect { get; init; }

    /// <summary>
    /// Whether the process is a SilverModManager. A process id in the sync file can be reused by another program once
    /// the main that wrote it has died.
    /// </summary>
    public required Func<Process, bool> IsOurProcess { get; init; }

    /// <summary>
    /// Empties the main slot, which names a process that is no longer a SilverModManager, if it still names that pid.
    /// </summary>
    public required Action<IServiceProvider, int> ClearStaleMain { get; init; }
    public required Action<IServiceProvider> OpenDatabase { get; init; }

    /// <summary>
    /// Takes the main slot in the sync file for a headless main, with port 0 because it runs no server.
    /// Returns false when another live process holds the slot or won the race for it.
    /// </summary>
    public required Func<IServiceProvider, MainKind, int, bool> TryClaimMain { get; init; }

    /// <summary>
    /// Clears the main slot, if this process still holds it.
    /// </summary>
    public required Action<IServiceProvider> ReleaseMain { get; init; }

    /// <summary>
    /// Closes the database so that nothing can write to a game folder after the launch session lets go of it.
    /// </summary>
    public required Action<IServiceProvider> CloseDatabase { get; init; }

    /// <summary>
    /// Ends the launch session of this process, if one ran. Called after <see cref="CloseDatabase"/>.
    /// </summary>
    public required Action<IServiceProvider> ReleaseLaunchSession { get; init; }

    /// <summary>
    /// Tells the person at the desktop why a start was refused, when there is no terminal to print to.
    /// </summary>
    public required Action<IServiceProvider, string> Notify { get; init; }
    public required Action<IServiceProvider> InstallGlobalHandlers { get; init; }
    public required Func<IServiceProvider, StartupMode, Task<int>> RunCliAsMain { get; init; }
    public required Func<IServiceProvider, StartupMode, Task<int>> RunCliRemotely { get; init; }

    /// <summary>
    /// Launches the game a <c>run</c> names and returns the exit code: the game's own, or 125 when the launch failed.
    /// </summary>
    public required Func<IServiceProvider, LaunchArgs, Task<int>> RunLaunch { get; init; }

    /// <summary>
    /// Runs the launch's command unchanged when the window with the given pid owns a running launch of the
    /// same game, and returns the command's exit code. Returns null when it does not, so the start is refused.
    /// </summary>
    public required Func<IServiceProvider, LaunchArgs, int, Task<int?>> TryPassThrough { get; init; }

    /// <summary>
    /// Hands the launch to the window with the given pid, which runs it and keeps the database. Returns once the
    /// game has ended. The stop signals this process receives meanwhile are passed on to the window.
    /// </summary>
    public required Func<IServiceProvider, LaunchArgs, Task<ForwardedLaunchOutcome>> ForwardLaunch { get; init; }

    /// <summary>
    /// The text that tells why a live headless main of this kind and pid is in the way.
    /// </summary>
    public required Func<IServiceProvider, MainKind, int, string> DescribeHeadlessMain { get; init; }
    public required Action<IServiceProvider> RunUi { get; init; }

    /// <summary>
    /// Serves MCP on stdio until the client closes it or the host stops, and returns the exit code. Runs in place of
    /// <see cref="RunCliRemotely"/>: it never starts a main process for a missing window.
    /// </summary>
    public required Func<IServiceProvider, StartupMode, Task<int>> RunMcp { get; init; }

    /// <summary>
    /// Reports a refusal to the person at the terminal.
    /// </summary>
    public Action<string> WriteError { get; init; } = message => Console.Error.WriteLine(message);
}

public static class ProcessStartup
{
    /// <summary>The exit code of a <c>run</c> that never got to launch, matching the launch session's own failures.</summary>
    private const int RunFailureExitCode = 125;
    private static readonly TimeSpan HostStopTimeout = TimeSpan.FromSeconds(15);

    private const int ClaimAttempts = 3;

    public const string DryRunNeedsHeadlessMessage = "SilverModManager is open, so --dry-run cannot read its loadouts. Close SilverModManager and run it again.";
    public const string StartingMessage = "SilverModManager is starting...";
    public const string SecretsFileNeedsHeadlessMessage = "a secrets file applies to a headless process; close the window first";
    public const string OpenedMeanwhileMessage = "SilverModManager opened while this command was starting. Run it again.";

    /// <summary>
    /// Why an open window turned a launch away after looking at it.
    /// </summary>
    public static string GuiDeclinedMessage(string reason) => $"SilverModManager is open and could not take this launch from Steam: {reason}";

    /// <summary>
    /// Why an open window could not even be asked, which includes a window from a build that does not take launches.
    /// </summary>
    public static string GuiUnavailableMessage(string reason) =>
        $"SilverModManager is open but could not take this launch from Steam ({reason}). Close SilverModManager, or start the game from inside it.";

    /// <summary>
    /// Reads the settings the process needs before it has a logger. A file that cannot be read is reported as a
    /// plain line on <paramref name="writeError"/>, since there is no log yet to hold the details.
    /// </summary>
    public static bool TryReadSettings<T>(Func<T> read, Action<string> writeError, [NotNullWhen(true)] out T? settings)
        where T : class
    {
        try
        {
            settings = read();
            return true;
        }
        catch (Exception ex)
        {
            // No log holds this failure, so the line carries the cause itself instead of pointing at one.
            writeError($"Could not read the settings: {ex.Message}");
            settings = null;
            return false;
        }
    }

    public static int Run(StartupMode startupMode, string[] args, ProcessStartupSteps steps)
    {
        // Program.Main answers these before any host exists. Here they would build one and exit 0 having done nothing,
        // and the host's ConsoleLifetime would take the SIGTERM a selftest helper waits for.
        if (startupMode.IsStatus || startupMode.IsRunHelp || startupMode.IsSelftestHelper)
            throw new InvalidOperationException($"'{startupMode.OriginalArgs[0]}' must be answered before a host is built");

        // A malformed launch needs no database, host or main slot to be turned away.
        LaunchArgs? launch = null;
        if (startupMode.IsHeadlessRun)
        {
            if (!LaunchArgs.TryParse(startupMode.Args, out launch, out var usageError))
            {
                steps.WriteError(usageError);
                return RunFailureExitCode;
            }
        }

        // The secret store is built from the file the command line names, so both the launch's and a credential verb's
        // option have to be on the mode before the host exists.
        if (launch is not null) startupMode.SecretsFile = launch.SecretsFile;
        if (startupMode.SecretsFileError is { } secretsFileError)
        {
            steps.WriteError(secretsFileError);
            return 1;
        }

        if (startupMode.McpUsageError is { } mcpUsageError)
        {
            steps.WriteError(mcpUsageError);
            return 1;
        }

        using var host = steps.BuildHost(startupMode);

        var services = host.Services;
        var logger = services.GetRequiredService<ILogger<Program>>();

        var headlessMain = startupMode.RunAsMain && !startupMode.ShowUI;
        var syncInfo = ClearStaleMain(services, logger, steps, steps.ReadSyncInfo(services));

        // A verb handed to a live main runs against that process's store, which never saw this file.
        if (!startupMode.RunAsMain && startupMode.UsesOwnCredentials && syncInfo.Process is not null && HasSecretsFileOverride(startupMode, services))
        {
            logger.LogWarning("Not running {Verb}: {Message}", startupMode.Args[0], SecretsFileNeedsHeadlessMessage);
            steps.WriteError(SecretsFileNeedsHeadlessMessage);
            return 1;
        }

        // A window cannot share the database with a process that holds it without a server to talk to.
        if (startupMode.RunAsMain && startupMode.ShowUI && syncInfo.Process is not null && syncInfo.Kind != MainKind.Gui)
        {
            var refusal = steps.DescribeHeadlessMain(services, syncInfo.Kind, syncInfo.Process.Id);
            logger.LogWarning("Not starting the UI: {Message}", refusal);
            steps.Notify(services, refusal);
            return 0;
        }

        // A launch never takes over from a live main: the window or the other launch keeps the database and the game folder.
        if (launch is not null && syncInfo.Process is { } holder)
        {
            var handled = HandLaunchToLiveMain(services, logger, steps, launch, syncInfo.Kind, holder);
            if (handled is { } exitCode) return exitCode;

            // The window closed while this process was deciding what to do with it: nothing holds the database any more.
        }

        // A headless main only needs the slot to be free, and the MCP server keeps stdout for the protocol, so neither asks
        // the window anything. For everyone else a window that does not answer is told to the person and left running:
        // it is most likely busy, and closing it would lose whatever it is in the middle of.
        if (!headlessMain && !startupMode.IsMcp && syncInfo is { Process: not null, Kind: MainKind.Gui } && !steps.CanConnect(services, syncInfo.Port).GetAwaiter().GetResult())
            return RefuseBusyMain(services, logger, steps, startupMode, syncInfo.Process.Id, syncInfo.Port);

        var slotClaimed = false;
        if (headlessMain)
        {
            var kind = startupMode.IsHeadlessRun ? MainKind.HeadlessRun : MainKind.HeadlessCli;
            var failureCode = startupMode.IsHeadlessRun ? RunFailureExitCode : 1;
            slotClaimed = TryClaim(services, kind, steps, out var refusal);
            if (!slotClaimed)
            {
                logger.LogWarning("Could not become the main process: {Message}", refusal);
                steps.WriteError(refusal);
                return failureCode;
            }
        }

        var hostStarted = false;
        var databaseOpened = false;
        try
        {
            // Okay to do wait here, as we are in the main process thread.
            host.StartAsync().Wait(timeout: TimeSpan.FromMinutes(5));
            hostStarted = true;

            if (startupMode.RunAsMain)
            {
                try
                {
                    // This will startup the MnemonicDb connection
                    steps.OpenDatabase(services);
                    databaseOpened = true;
                }
                catch (Exception ex) when (headlessMain && IsDatabaseLocked(ex))
                {
                    // Another process still has the database open, most likely one that is shutting down.
                    logger.LogWarning(ex, "The database is locked by another process");
                    steps.WriteError(StartingMessage);
                    return startupMode.IsHeadlessRun ? RunFailureExitCode : 1;
                }
                catch (Exception ex)
                {
                    // Opening runs the migrations, so this fails every start until it is fixed. A window or a game
                    // launch has no terminal, so stderr alone would look like the app never started.
                    logger.LogFailure(ex, "Could not open the database");
                    var failure = CliErrors.Describe(ex);
                    steps.WriteError(failure);
                    if (startupMode.ShowUI || startupMode.StartsUiWhenNoMainRunning || startupMode.IsHeadlessRun) steps.Notify(services, failure);
                    return startupMode.IsHeadlessRun ? RunFailureExitCode : 1;
                }
            }

            // Start the CLI server if we are the main process.
            var cliServer = services.GetService<CliServer>();
            cliServer?.StartCliServerAsync().Wait(timeout: TimeSpan.FromSeconds(5));

            LogMessages.RuntimeInformation(logger, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, ApplicationConstants.InstallationMethod);
            steps.InstallGlobalHandlers(services);

            try
            {
                if (startupMode.RunAsMain)
                {
                    LogMessages.StartingProcess(logger, Environment.ProcessPath, Environment.ProcessId, args);

                    if (launch is not null) return steps.RunLaunch(services, launch).GetAwaiter().GetResult();

                    var task = steps.RunCliAsMain(services, startupMode);
                    if (startupMode.ShowUI) steps.RunUi(services);
                    return task.Result;
                }

                if (startupMode.IsMcp) return steps.RunMcp(services, startupMode).GetAwaiter().GetResult();

                return steps.RunCliRemotely(services, startupMode).GetAwaiter().GetResult();
            }
            catch (HeadlessMainRunning ex)
            {
                var refusal = steps.DescribeHeadlessMain(services, ex.Kind, ex.ProcessId);
                steps.WriteError(refusal);

                // A link is clicked in a browser, which shows no stderr.
                if (startupMode.StartsUiWhenNoMainRunning) steps.Notify(services, refusal);
                return 1;
            }
            catch (Exception ex)
            {
                logger.LogFailure(ex, "Unhandled exception");

                // Steam starts the launch option without a terminal, so the log alone would leave the game silently not starting.
                var failure = CliErrors.Describe(ex);
                steps.WriteError(failure);

                // Like the headless refusal above: a link clicked in a browser shows no stderr.
                if (startupMode.StartsUiWhenNoMainRunning && ex is VersionMismatch or MainProcessSilent) steps.Notify(services, failure);
                if (!startupMode.IsHeadlessRun) return 1;

                steps.Notify(services, failure);
                return RunFailureExitCode;
            }
        }
        finally
        {
            // Wait for 15 seconds for the host to stop, otherwise kill the process
            var stopped = !hostStarted || host.StopAsync().Wait(timeout: HostStopTimeout);

            // Written before the teardown so the sinks are still up; otherwise the log ends on whatever service logged last.
            if (!stopped) logger.LogWarning("The host did not stop within {Seconds} s; the process will exit without waiting for it", HostStopTimeout.TotalSeconds);

            // Database first, then the launch session's lock, then the slot: a process that sees the slot
            // free may open the database and a game folder at once.
            if (startupMode.RunAsMain) TearDownMain(services, logger, steps, databaseOpened);

            if (!stopped) Environment.Exit(0);
        }
    }

    /// <summary>
    /// Settles a <c>run</c> that finds a live main: joins or forwards to the window, or refuses. Returns the exit code,
    /// or null when the window closed before the launch could be handed to it, so the caller can run it itself.
    /// </summary>
    private static int? HandLaunchToLiveMain(IServiceProvider services, ILogger logger, ProcessStartupSteps steps, LaunchArgs launch, MainKind kind, Process holder)
    {
        // A dry run starts nothing, and passing through or forwarding would run the command.
        if (kind != MainKind.Gui || launch.DryRun)
        {
            var refusal = kind == MainKind.Gui
                ? DryRunNeedsHeadlessMessage
                : steps.DescribeHeadlessMain(services, kind, holder.Id);
            return Refuse(services, logger, steps, refusal);
        }

        try
        {
            // The window started this game through Steam and owns the session; the wrapper only reports in.
            if (steps.TryPassThrough(services, launch, holder.Id).GetAwaiter().GetResult() is { } exitCode)
            {
                // The launch needs no Nexus credential, and failing a Steam launch because the window is open would be worse.
                WarnSecretsFileIgnored(logger, launch);
                return exitCode;
            }

            // Steam's own Play: the window runs the launch the way a headless main would, and this process
            // stays only to hold Steam's session open and to relay its stop.
            var outcome = steps.ForwardLaunch(services, launch).GetAwaiter().GetResult();
            if (outcome is ForwardedLaunchOutcome.Exited exited)
            {
                WarnSecretsFileIgnored(logger, launch);

                // The window has already told the desktop; Steam's log gets the words too.
                if (exited.Message is { } message) steps.WriteError(message);
                return exited.ExitCode;
            }

            var windowRefusal = outcome switch
            {
                ForwardedLaunchOutcome.Declined declined => GuiDeclinedMessage(declined.Reason),
                ForwardedLaunchOutcome.Unavailable unavailable => GuiUnavailableMessage(unavailable.Reason),
                _ => throw new InvalidOperationException($"Unknown outcome {outcome}"),
            };
            return Refuse(services, logger, steps, windowRefusal);
        }
        catch (NoMainProcessStarted)
        {
            logger.LogInformation("The window closed before the launch was handed to it, running the launch here instead");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogFailure(ex, "Could not hand the launch to the running window");
            var failure = CliErrors.Describe(ex);
            steps.WriteError(failure);
            steps.Notify(services, failure);
            return CliErrors.ExitCode(ex);
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is a database that could not be opened because another process
    /// holds its lock file.
    /// </summary>
    public static bool IsDatabaseLocked(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("While lock file", StringComparison.OrdinalIgnoreCase)) return true;
            if (message.Contains("lock hold by current process", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static int Refuse(IServiceProvider services, ILogger logger, ProcessStartupSteps steps, string refusal)
    {
        logger.LogWarning("Not launching: {Message}", refusal);
        steps.WriteError(refusal);

        // Steam starts a launch option without a terminal to print to.
        steps.Notify(services, refusal);
        return RunFailureExitCode;
    }

    private static void WarnSecretsFileIgnored(ILogger logger, LaunchArgs launch)
    {
        if (launch.SecretsFile is { } ignored)
            logger.LogWarning("--secrets-file {Path} is ignored: the running window owns this launch and keeps its own credentials", ignored);
    }

    private static bool HasSecretsFileOverride(StartupMode startupMode, IServiceProvider services)
        => startupMode.SecretsFile is not null
           || !string.IsNullOrEmpty(services.GetService<IEnvironmentReader>()?.Get(SilverModManager.Secrets.Services.SecretsFileEnvironmentVariable));

    private static bool TryClaim(IServiceProvider services, MainKind kind, ProcessStartupSteps steps, out string refusal)
    {
        refusal = "";
        for (var attempt = 0; attempt < ClaimAttempts; attempt++)
        {
            if (steps.TryClaimMain(services, kind, 0)) return true;

            // A dead pid in the slot loses the race for the claim without anyone holding it: try again.
            var (winner, winnerKind, _) = steps.ReadSyncInfo(services);
            if (winner is null) continue;

            refusal = winnerKind == MainKind.Gui
                ? OpenedMeanwhileMessage
                : steps.DescribeHeadlessMain(services, winnerKind, winner.Id);
            return false;
        }

        refusal = "Could not become the main process: the sync file kept changing underneath us. Run it again.";
        return false;
    }

    private static void TearDownMain(IServiceProvider services, ILogger logger, ProcessStartupSteps steps, bool databaseOpened)
    {
        // Each step runs even if an earlier one threw: the slot has to be freed whatever happened.
        if (databaseOpened) Guarded(logger, "close the database", () => steps.CloseDatabase(services));
        Guarded(logger, "release the launch session", () => steps.ReleaseLaunchSession(services));
        Guarded(logger, "release the main process slot", () => steps.ReleaseMain(services));
    }

    private static void Guarded(ILogger logger, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to {What}", what);
        }
    }

    /// <summary>
    /// Forgets a main whose process id now belongs to another program. The sync file holds only a pid, and once the main
    /// that wrote it has died anything may be given the same number; the slot would stay taken for as long as that program
    /// runs. Nothing is closed: the process is not ours to touch.
    /// </summary>
    private static (Process? Process, MainKind Kind, int Port) ClearStaleMain(IServiceProvider services, ILogger logger, ProcessStartupSteps steps, (Process? Process, MainKind Kind, int Port) syncInfo)
    {
        if (syncInfo.Process is not { } holder || steps.IsOurProcess(holder)) return syncInfo;

        logger.LogWarning("The sync file names process {PID}, which is not a SilverModManager; clearing the entry", holder.Id);
        steps.ClearStaleMain(services, holder.Id);
        return steps.ReadSyncInfo(services);
    }

    /// <summary>
    /// Turns a start away because the open window did not answer, and says so the way the start can be told it: on stderr
    /// for a command, and on the desktop for anything that was not typed in a terminal.
    /// </summary>
    private static int RefuseBusyMain(IServiceProvider services, ILogger logger, ProcessStartupSteps steps, StartupMode startupMode, int pid, int port)
    {
        var busy = new MainProcessBusy();
        logger.LogWarning("The main process {PID} on port {Port} did not answer; leaving it running and not starting", pid, port);

        var message = CliErrors.Describe(busy);
        steps.WriteError(message);
        var asksFromDesktop = startupMode.ShowUI || startupMode.StartsUiWhenNoMainRunning;
        if (asksFromDesktop) steps.Notify(services, message);

        // Opening the window again is not a failure of the person's command; a verb that could not run is.
        return startupMode.ShowUI ? 0 : CliErrors.ExitCode(busy);
    }
}

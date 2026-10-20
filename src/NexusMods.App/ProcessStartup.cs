using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Sdk;
using NexusMods.SingleProcess;
using NexusMods.SingleProcess.Exceptions;

namespace NexusMods.App;

/// <summary>
/// The parts of process startup that need a real host, a real sync file or a window.
/// <see cref="Program"/> supplies the real ones, tests supply fakes.
/// </summary>
public sealed record ProcessStartupSteps
{
    public required Func<StartupMode, IHost> BuildHost { get; init; }
    public required Func<IServiceProvider, (Process? Process, MainKind Kind, int Port)> ReadSyncInfo { get; init; }
    public required Func<IServiceProvider, int, Task<bool>> CanConnect { get; init; }
    public required Action<Process> Kill { get; init; }
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
    public required Action<IServiceProvider> RunUi { get; init; }

    /// <summary>
    /// Reports a refusal to the person at the terminal.
    /// </summary>
    public Action<string> WriteError { get; init; } = message => Console.Error.WriteLine(message);
}

public static class ProcessStartup
{
    /// <summary>The exit code of a <c>run</c> that never got to launch, matching the launch session's own failures.</summary>
    private const int RunFailureExitCode = 125;

    private const int ClaimAttempts = 3;

    public const string StartingMessage = "SilverModManager is starting...";
    public const string OpenedMeanwhileMessage = "SilverModManager opened while this command was starting. Run it again.";

    public static int Run(StartupMode startupMode, string[] args, ProcessStartupSteps steps)
    {
        using var host = steps.BuildHost(startupMode);

        var services = host.Services;
        var logger = services.GetRequiredService<ILogger<Program>>();

        var headlessMain = startupMode.RunAsMain && !startupMode.ShowUI;
        var syncInfo = steps.ReadSyncInfo(services);

        // A window cannot share the database with a process that holds it without a server to talk to.
        if (startupMode.RunAsMain && startupMode.ShowUI && syncInfo.Process is not null && syncInfo.Kind != MainKind.Gui)
        {
            var refusal = new HeadlessMainRunning(syncInfo.Kind, syncInfo.Process.Id).Message;
            logger.LogWarning("Not starting the UI: {Message}", refusal);
            steps.Notify(services, refusal);
            return 0;
        }

        // NOTE(erri120): has to come before host startup
        // A headless main must not kill the process it is queueing behind, and a hung window
        // is not its to judge: it only needs the slot to be free.
        if (!headlessMain)
            CleanupUnresponsiveProcesses(services, logger, steps, syncInfo).Wait(timeout: TimeSpan.FromSeconds(10));

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

                    var task = steps.RunCliAsMain(services, startupMode);
                    if (startupMode.ShowUI) steps.RunUi(services);
                    return task.Result;
                }

                return steps.RunCliRemotely(services, startupMode).GetAwaiter().GetResult();
            }
            catch (HeadlessMainRunning ex)
            {
                steps.WriteError(ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception");
                return 1;
            }
        }
        finally
        {
            // Wait for 15 seconds for the host to stop, otherwise kill the process
            var stopped = !hostStarted || host.StopAsync().Wait(timeout: TimeSpan.FromSeconds(15));

            // Database first, then the launch session's lock, then the slot: a process that sees the slot
            // free may open the database and a game folder at once.
            if (startupMode.RunAsMain) TearDownMain(services, logger, steps, databaseOpened);

            if (!stopped) Environment.Exit(0);
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
                : new HeadlessMainRunning(winnerKind, winner.Id).Message;
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

    private static async Task CleanupUnresponsiveProcesses(IServiceProvider services, ILogger logger, ProcessStartupSteps steps, (Process? Process, MainKind Kind, int Port) syncInfo)
    {
        // NOTE(erri120): this is a hack, see https://github.com/Nexus-Mods/NexusMods.App/issues/3633
        var (process, kind, port) = syncInfo;
        if (process is null) return;

        // A headless main runs no CLI server, so a failed heartbeat says nothing about it.
        if (kind != MainKind.Gui) return;

        var pid = process.Id;
        var canConnect = await steps.CanConnect(services, port);
        if (canConnect) return;

        logger.LogWarning("Unable to connect to old process with PID `{PID}` on port `{Port}`, force closing process", pid, port);

        try
        {
            steps.Kill(process);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Exception killing old process `{PID}`", pid);
        }
    }
}

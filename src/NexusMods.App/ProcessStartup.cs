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
    public static int Run(StartupMode startupMode, string[] args, ProcessStartupSteps steps)
    {
        using var host = steps.BuildHost(startupMode);

        var services = host.Services;
        var logger = services.GetRequiredService<ILogger<Program>>();

        // NOTE(erri120): has to come before host startup
        CleanupUnresponsiveProcesses(services, logger, steps).Wait(timeout: TimeSpan.FromSeconds(10));

        // Okay to do wait here, as we are in the main process thread.
        host.StartAsync().Wait(timeout: TimeSpan.FromMinutes(5));

        if (startupMode.RunAsMain)
        {
            // This will startup the MnemonicDb connection
            steps.OpenDatabase(services);
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
        finally
        {
            // Wait for 15 seconds for the host to stop, otherwise kill the process
            if (!host.StopAsync().Wait(timeout: TimeSpan.FromSeconds(15)))
                Environment.Exit(0);
        }
    }

    private static async Task CleanupUnresponsiveProcesses(IServiceProvider services, ILogger logger, ProcessStartupSteps steps)
    {
        // NOTE(erri120): this is a hack, see https://github.com/Nexus-Mods/NexusMods.App/issues/3633
        var (process, kind, port) = steps.ReadSyncInfo(services);
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

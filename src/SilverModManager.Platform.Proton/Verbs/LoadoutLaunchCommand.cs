using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Platform.Proton.Verbs;

/// <summary>
/// Starts a loadout's game the way the Play button does, and returns once the launch is under way instead of when the
/// game exits.
/// </summary>
public sealed class LoadoutLaunchCommand(IServiceProvider services, IToolManager tools, IJobMonitor monitor, ILogger<LoadoutLaunchCommand> logger)
{
    public const int FailureExitCode = 1;

    // A launch that has not failed after an hour is not going to; the bound also keeps Task.Delay's own limit out of reach.
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    public async Task<int> RunAsync(IRenderer renderer, Loadout.ReadOnly loadout, TimeSpan wait)
    {
        if (wait < TimeSpan.Zero || wait > MaxWait)
        {
            await renderer.TextLine($"--wait takes a number of seconds from 0 to {MaxWait.TotalSeconds:0}.");
            return FailureExitCode;
        }

        if (services.GetService<WindowHost>() is null)
        {
            await renderer.TextLine("Launching needs the SilverModManager window to be open, because it keeps the game running after this command ends. Open SilverModManager and try again.");
            return FailureExitCode;
        }

        var runTool = tools.GetTools(loadout).OfType<IRunGameTool>().FirstOrDefault();
        if (runTool is null)
        {
            await renderer.TextLine($"The game of loadout '{loadout.Name}' cannot be launched from SilverModManager.");
            return FailureExitCode;
        }

        // Detached on purpose: the game outlives this command, so nothing here may cancel it. Whatever it throws
        // after the wait has nobody to be shown to, so the log is the only place left for it. The task hands the
        // failure back instead of rethrowing it: a faulted task nobody awaits would be logged a second time, as an
        // unobserved task exception at Error, even when it was only a refusal.
        var launch = Task.Run<Exception?>(async () =>
        {
            try
            {
                await tools.RunTool(runTool, loadout, monitor);
                return null;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Launch of loadout {Loadout} was cancelled", loadout.Name);
                return null;
            }
            catch (Exception ex)
            {
                logger.LogFailure(ex, "Launching loadout {Loadout} failed", loadout.Name);
                return ex;
            }
        });

        var finished = await Task.WhenAny(launch, Task.Delay(wait));
        if (finished == launch && await launch is { } failure)
        {
            var refusal = UserFacingErrors.Find(failure);
            await renderer.TextLine(refusal?.Message ?? $"Launching '{loadout.Name}' failed. The SilverModManager log has the details.");
            return FailureExitCode;
        }

        // A clean end inside the wait proves nothing: when Steam never starts the game that is a desktop
        // notification, not an exception. A slow apply can also still be running, so the text cannot claim it is done.
        await renderer.TextLine($"The launch applies loadout '{loadout.Name}' and then starts its game; launch in progress. Run `status` (the app_status tool over MCP) to follow it.");
        return 0;
    }
}

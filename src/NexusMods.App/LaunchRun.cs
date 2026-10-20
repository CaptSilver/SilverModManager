using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.App;

/// <summary>
/// What a <c>run</c> process does once it is the main process: finds the loadout, then applies it, starts
/// the command Steam handed over and takes the game's changes back.
/// </summary>
public static class LaunchRun
{
    /// <summary>
    /// Returns the game's exit code, or <see cref="LaunchSession.FailureExitCode"/> when the launch never got
    /// that far. Every failure has gone to stderr, the log and the desktop by the time this returns.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, LaunchArgs args)
    {
        var resolution = services.GetRequiredService<LoadoutResolver>().Resolve(args);
        if (!resolution.IsResolved)
        {
            // Steam starts the launch option without a terminal to print to.
            services.GetRequiredService<ILogger<LaunchSession>>().LogError("{Message}", resolution.Error);
            Console.Error.WriteLine(resolution.Error);
            services.GetRequiredService<IDesktopNotifier>().Notify(ApplicationConstants.ProductName, resolution.Error!);
            return resolution.ExitCode;
        }

        var loadout = resolution.Loadout!.Value;
        var installation = resolution.Installation!;
        var tools = services.GetRequiredService<IToolManager>();

        // The slot is what ProcessStartup ends once the database is closed.
        var session = services.GetRequiredService<LaunchSession>();
        services.GetRequiredService<LaunchSessionSlot>().Current = session;

        var context = new LaunchContext(installation, loadout.LoadoutId, loadout.Name);
        var result = await session.RunAsync(context, () => PlanFor(tools, loadout, installation, args.Command));
        return result.ExitCode;
    }

    private static LaunchPlan PlanFor(IToolManager tools, Loadout.ReadOnly loadout, GameInstallation installation, IReadOnlyList<string> command)
    {
        // What the run tool resolves can depend on files the apply just put in place, so it reads the loadout afresh.
        var current = loadout.Rebase();
        var runTool = tools.GetTools(current).OfType<IRunGameTool>().FirstOrDefault();
        if (runTool is null) return new LaunchPlan(new GameProcessSpec(command));

        var spec = runTool.GetLaunchSpec(current, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var primaryExe = installation.Locations.ToAbsolutePath(installation.Game.GetPrimaryFile(installation));
        return LaunchPlanner.Compose(command, installation.Locations[LocationId.Game].Path, primaryExe, spec);
    }
}

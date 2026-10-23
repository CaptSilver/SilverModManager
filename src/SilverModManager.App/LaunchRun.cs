using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Platform.Proton;
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
            services.GetRequiredService<IDesktopNotifier>().Notify(ApplicationConstants.ProductName, resolution.Error);
            return resolution.ExitCode;
        }

        var loadout = resolution.Loadout.Value;
        var installation = resolution.Installation;

        var session = services.GetRequiredService<LaunchSession>();
        var wineDllOverrides = WineDllOverrideRequirements.For(installation.Game, loadout);
        var context = new LaunchContext(installation, loadout.LoadoutId, loadout.Name, wineDllOverrides);

        // A dry run takes no lock, so there is no session for the slot to end.
        if (args.DryRun) return session.DryRun(context, () => PlanFor(services, loadout, installation, args.Command, wineDllOverrides), Console.Out).ExitCode;

        // The slot is what ProcessStartup ends once the database is closed.
        services.GetRequiredService<LaunchSessionSlot>().Current = session;
        var result = await session.RunAsync(context, () => PlanFor(services, loadout, installation, args.Command, wineDllOverrides));
        return result.ExitCode;
    }

    /// <summary>
    /// Joins the launch the window <paramref name="guiPid"/> is running: records this wrapper in the launch's
    /// state file and runs the command with the exe and arguments the window recorded. Returns the command's
    /// exit code, or null when the window has no running launch of this game.
    /// </summary>
    public static Task<int?> TryPassThroughAsync(IServiceProvider services, LaunchArgs args, int guiPid) =>
        WrapperPassThrough.TryRunAsync(args.Command, services.GetRequiredService<IEnvironmentReader>(), guiPid, services.GetRequiredService<ILogger<LaunchSession>>(), notifier: services.GetRequiredService<IDesktopNotifier>());

    private static LaunchPlan PlanFor(IServiceProvider services, Loadout.ReadOnly loadout, GameInstallation installation, IReadOnlyList<string> command, IReadOnlyList<WineDllOverride> wineDllOverrides)
    {
        // What the run tool resolves can depend on files the apply just put in place, so it reads the loadout afresh.
        var current = loadout.Rebase();
        var tools = services.GetRequiredService<IToolManager>();
        var launchRoots = services.GetRequiredService<DeploymentLaunchRoots>().For(installation, current.InstallationId);
        var runTool = tools.GetTools(current).OfType<IRunGameTool>().FirstOrDefault();
        // The requirement is the game's, not the tool's, so it goes on the spec with or without a run tool.
        if (runTool is null) return new LaunchPlan(new GameProcessSpec(command, FilesystemRoots: launchRoots, WineDllOverrides: wineDllOverrides));

        var spec = runTool.GetLaunchSpec(current, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var primaryExe = installation.Locations.ToAbsolutePath(installation.Game.GetPrimaryFile(installation));
        return LaunchPlanner.Compose(command, installation.Locations[LocationId.Game].Path, primaryExe, spec, launchRoots, wineDllOverrides);
    }
}

using System.Collections;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Platform.Proton;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.SingleProcess;

namespace SilverModManager.App;

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
            services.GetRequiredService<ILogger<LaunchSession>>().LogWarning("{Message}", resolution.Error);
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

    /// <summary>
    /// Hands the launch to the running window and waits for the game to end, passing Steam's stop on to it. The
    /// window runs the same launch session a headless <c>run</c> would, so this process only keeps Steam's own
    /// session open and relays the exit code.
    /// </summary>
    public static async Task<ForwardedLaunchOutcome> ForwardAsync(IServiceProvider services, LaunchArgs args)
    {
        using var stop = new CancellationTokenSource();
        void Stop(PosixSignalContext context)
        {
            // Steam's Stop sends SIGTERM to this process alone; the game is the window's child, so the window has to end it.
            context.Cancel = true;
            stop.Cancel();
        }

        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using var hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);

        var client = services.GetRequiredService<CliClient>();
        return await client.ForwardLaunchAsync(ToForwardedLaunch(args, CurrentEnvironment(), Environment.CurrentDirectory), stop.Token);
    }

    /// <summary>
    /// What the window needs to run <paramref name="args"/> as this process would: the environment Steam gave it and
    /// the directory Steam started it in.
    /// </summary>
    public static ForwardedLaunch ToForwardedLaunch(LaunchArgs args, IReadOnlyDictionary<string, string> environment, string? workingDirectory)
        => new(args.LoadoutName, args.Game, args.Command, environment, workingDirectory);

    private static Dictionary<string, string> CurrentEnvironment()
    {
        var result = new Dictionary<string, string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            result[(string)entry.Key] = (string?)entry.Value ?? "";
        return result;
    }

    /// <summary>
    /// Runs a launch a <c>run</c> process handed over, in this process and in the environment that process had. The
    /// window keeps its database open, so the session lets go of the installation as soon as the game is done.
    /// <paramref name="stop"/> ends the game the way Steam's Stop would have.
    /// </summary>
    public static async Task<ForwardedLaunchExit> RunForwardedAsync(IServiceProvider services, Loadout.ReadOnly loadout, GameInstallation installation, ForwardedLaunch launch, CancellationToken stop)
    {
        var wineDllOverrides = WineDllOverrideRequirements.For(installation.Game, loadout);
        var context = new LaunchContext(installation, loadout.LoadoutId, loadout.Name, wineDllOverrides);

        var session = services.GetRequiredService<LaunchSession>();
        try
        {
            using var stopping = stop.Register(session.RequestStop);
            var result = await session.RunAsync(context, () => InForwardedEnvironment(PlanFor(services, loadout, installation, launch.Command, wineDllOverrides), launch));
            return new ForwardedLaunchExit(result.ExitCode, result.Message);
        }
        finally
        {
            session.Release();
        }
    }

    // The game runs in what Steam gave the run process, in the folder Steam started it in; the window's own are unrelated.
    private static LaunchPlan InForwardedEnvironment(LaunchPlan plan, ForwardedLaunch launch)
        => plan with
        {
            Process = plan.Process with
            {
                BaseEnvironment = launch.Environment,
                WorkingDirectory = plan.Process.WorkingDirectory ?? launch.WorkingDirectory,
            },
        };

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

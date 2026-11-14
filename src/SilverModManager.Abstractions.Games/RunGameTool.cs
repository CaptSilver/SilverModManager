using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games.Starters;
using SilverModManager.Abstractions.Loadouts;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using R3;

namespace SilverModManager.Abstractions.Games;

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

    /// <summary>
    /// True while the wait is only Steam running the game's install step (redistributables and the like) before
    /// it starts the game. Emits the current value on subscribe.
    /// </summary>
    IObservable<bool> RunningInstallStep { get; }
}

/// <summary>
/// A tool that launches the game, using first found installation.
/// </summary>
/// <remarks>
/// Takes the game as an instance, so a game built from a data record, which has no type of its own, can have one.
/// <see cref="RunGameTool{T}"/> is the same tool for a game that is a registered type.
/// </remarks>
public class RunGameTool : IRunGameTool
{
    private readonly ILogger _logger;
    private readonly IGame _game;
    private readonly IGameLaunchSession _launchSessions;
    private readonly IGameStarterSelector _starters;
    private readonly SteamGameStarter _steam;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Whether this tool should be started through the shell instead of directly.
    /// This allows tools to start their own console, allowing users to interact with it.
    /// </summary>
    protected virtual bool UseShell { get; set; } = false;

    /// <summary>
    /// How long Steam's reaper for the game gets to show up once Steam is running. A launcher or dialog Steam is
    /// waiting on is not something to sit behind for minutes, so this is short; a launch wrapper that has
    /// registered itself extends it.
    /// </summary>
    protected virtual TimeSpan StartTimeout => TimeSpan.FromSeconds(60);

    /// <summary>How long Steam itself gets to be running after the URI is opened; it may be starting from cold.</summary>
    protected virtual TimeSpan SteamStartTimeout => TimeSpan.FromMinutes(2);

    /// <summary>How often the process table is looked at while waiting.</summary>
    protected virtual TimeSpan PollInterval => TimeSpan.FromMilliseconds(300);

    /// <summary>The clock the waits run on; tests replace it so they never sit through a real timeout.</summary>
    protected virtual TimeProvider Clock => TimeProvider.System;

    /// <summary>
    /// How long the game's processes must stay gone before a program this tool started itself counts as finished.
    /// A launcher can exit a few hundred milliseconds before the game's own process shows up.
    /// </summary>
    protected virtual TimeSpan Settle => SettlePeriods.Direct;

    /// <summary>
    /// The same for a game Steam started, where the client can take seconds to hand the game its reaper again,
    /// for example when a game relaunches itself.
    /// </summary>
    protected virtual TimeSpan SteamSettle => SettlePeriods.Steam;

    /// <inheritdoc />
    public IObservable<bool> WaitingForSteam => _steam.WaitingForSteam;

    /// <inheritdoc />
    public IObservable<bool> RunningInstallStep => _steam.RunningInstallStep;

    /// <summary>
    /// Constructor
    /// </summary>
    public RunGameTool(IServiceProvider serviceProvider, IGame game)
        : this(serviceProvider, game, serviceProvider.GetRequiredService<ILogger<RunGameTool>>())
    {
    }

    /// <summary>
    /// For a subclass that wants its own log category.
    /// </summary>
    protected RunGameTool(IServiceProvider serviceProvider, IGame game, ILogger logger)
    {
        _game = game;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _launchSessions = serviceProvider.GetRequiredService<IGameLaunchSession>();
        _starters = serviceProvider.GetRequiredService<IGameStarterSelector>();
        _steam = new SteamGameStarter(
            serviceProvider.GetRequiredService<ISteamUriLauncher>(),
            serviceProvider.GetRequiredService<IProcessTracker>(),
            serviceProvider.GetRequiredService<IRunningGameDetector>(),
            logger);
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
        var installation = loadout.InstallationInstance;
        var primaryFile = installation.Locations.ToAbsolutePath(_game.GetPrimaryFile(installation));
        var request = new GameStartRequest(loadout, installation, spec, commandLineArgs ?? [], spec.Exe.Equals(primaryFile), UseShell, Timing);

        var choice = _starters.For(request, _steam);
        // Asked before the session, so the loadout is not applied for a launch that cannot run.
        choice.Starter.CheckCanStart(request);

        var outcome = GameStartOutcome.Cancelled;
        // The Steam launch option only ever substitutes the game's own exe, so a launcher's session carries no spec.
        using (var launch = _launchSessions.BeginSession(loadout, request.IsPrimaryExe ? spec : null))
        {
            // The starter runs only once the session has the lock and the loadout is applied, so a client never
            // starts the game against a half-applied folder.
            await launch.RunAsync(async token =>
            {
                if (choice.FallbackNotice is { } notice)
                {
                    _logger.LogInformation("{Notice}", notice);
                    _serviceProvider.GetRequiredService<IDesktopNotifier>().Notify(ApplicationConstants.ProductName, notice);
                }
                outcome = await choice.Starter.Start(request, launch, token);
            }, cancellationToken);
        }

        // Said after the session has finished, so the user is not told to retry while the lock is still held.
        if (choice.Starter.AfterSessionMessage(request, outcome) is { } message)
            _serviceProvider.GetRequiredService<IDesktopNotifier>().Notify(ApplicationConstants.ProductName, message);
    }

    private StartTiming Timing => new(StartTimeout, SteamStartTimeout, PollInterval, Settle, SteamSettle, Clock);

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

/// <summary>
/// <see cref="RunGameTool"/> for a game that is a registered type. Subclasses of this are the tools that add
/// launch behaviour (a script extender, a launcher) to one game.
/// </summary>
/// <typeparam name="T">The game; its log category is this tool's.</typeparam>
public class RunGameTool<T>(IServiceProvider serviceProvider, T game)
    : RunGameTool(serviceProvider, game, serviceProvider.GetRequiredService<ILogger<RunGameTool<T>>>())
    where T : IGame;

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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.SingleProcess;

namespace SilverModManager.App;

/// <summary>
/// Runs the launch a Steam launch option's <c>run</c> process hands to the window, so pressing Play in Steam
/// works while the window is open. The window already holds the database and the game folder, which is why
/// <c>run</c> cannot start a second owner of either.
/// </summary>
public sealed class ForwardedLaunchHandler(IServiceProvider services) : IForwardedLaunchHandler
{
    /// <inheritdoc />
    public Task<ForwardedLaunchAdmission> AdmitAsync(ForwardedLaunch launch)
    {
        var logger = services.GetRequiredService<ILogger<ForwardedLaunchHandler>>();
        var args = new LaunchArgs(launch.LoadoutName, launch.Game, [..launch.Command]);

        // Steam's variables decide which game the launch is for, and they are the run process's, not the window's.
        var resolver = new LoadoutResolver(
            services.GetRequiredService<IConnection>(),
            services.GetRequiredService<IGameRegistry>(),
            new DictionaryEnvironmentReader(launch.Environment));
        var resolution = resolver.Resolve(args);
        if (!resolution.IsResolved)
        {
            logger.LogWarning("Not running a forwarded launch: {Message}", resolution.Error);
            return Task.FromResult(ForwardedLaunchAdmission.Declined(resolution.Error));
        }

        var installation = resolution.Installation;
        var game = installation.Game.DisplayName;
        if (services.GetRequiredService<ActiveLaunchSessions>().IsRunning(installation))
            return Declined(logger, $"{game} is already running through SilverModManager.");

        var connection = services.GetRequiredService<IConnection>();
        var installationId = resolution.Loadout.Value.InstallationId;
        if (ApplyInProgress(services.GetRequiredService<IJobMonitor>().Jobs, id =>
            {
                var other = Loadout.Load(connection.Db, id);
                return other.IsValid() && other.InstallationId == installationId;
            }))
            return Declined(logger, $"SilverModManager is applying a loadout to {game}. Wait for it to finish, then press Play in Steam again.");

        logger.LogInformation("Running {Loadout} for {Game} on behalf of a Steam launch", resolution.Loadout.Value.Name, game);
        return Task.FromResult(ForwardedLaunchAdmission.Accepted(stop => LaunchRun.RunForwardedAsync(services, resolution.Loadout.Value, installation, launch, stop)));
    }

    /// <summary>
    /// Whether an apply or ingest is running for a loadout of the installation, which the launch would otherwise queue
    /// behind without anyone seeing why the game does not start.
    /// </summary>
    /// <param name="jobs">The jobs the monitor knows about.</param>
    /// <param name="isOfInstallation">Whether the loadout with this id belongs to the installation being launched.</param>
    public static bool ApplyInProgress(IEnumerable<IJob> jobs, Func<LoadoutId, bool> isOfInstallation)
        => jobs.ToArray().Any(job =>
            job.Status is JobStatus.Created or JobStatus.Running or JobStatus.Paused &&
            job.Definition is SynchronizeLoadoutJob sync &&
            isOfInstallation(sync.LoadoutId));

    private static Task<ForwardedLaunchAdmission> Declined(ILogger logger, string reason)
    {
        logger.LogWarning("Not running a forwarded launch: {Message}", reason);
        return Task.FromResult(ForwardedLaunchAdmission.Declined(reason));
    }
}

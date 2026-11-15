using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Starters;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch.Starters;

/// <summary>
/// Starts a loadout's replaced command (a sandbox around a mod loader, say) as a process of this app and waits
/// for it. Going through Steam would record the spec, which refuses an executable outside the game folder, and
/// the tool runner would take the command for a Windows program.
/// </summary>
public sealed class ProcessGameStarter(IGameProcessFactory processes, DeploymentLaunchRoots launchRoots) : IGameStarter
{
    /// <inheritdoc />
    public GameStarterKind Kind => GameStarterKind.Process;

    /// <inheritdoc />
    public bool CanCarryArguments(GameStartRequest request) => true;

    /// <summary>
    /// Refuses the caller's arguments: the command is the whole launch, and what it would do with them is unknown.
    /// <c>Spec.ExtraArgs</c> are not checked, since they are the command's own argv.
    /// </summary>
    public void CheckCanStart(GameStartRequest request)
    {
        if (request.CallerArgs.Length > 0)
            throw new GameLaunchException($"Arguments for {request.GameName} are not supported when its loadout replaces the launch command: {MarkdownText.Code(string.Join(' ', request.CallerArgs))}");
    }

    /// <inheritdoc />
    public async Task<GameStartOutcome> Start(GameStartRequest request, IGameLaunch launch, CancellationToken token)
    {
        var installation = request.Installation;
        var plan = LaunchPlanner.Compose(
            [], installation.Locations[LocationId.Game].Path,
            installation.Locations.ToAbsolutePath(installation.GetGame().GetPrimaryFile(installation)),
            request.Spec, launchRoots.For(installation, request.Loadout.InstallationId),
            WineDllOverrideRequirements.For(installation.Game, request.Loadout), HdrLaunchEnvironment.For(request.Loadout));

        using var process = processes.Start(plan.Process, installation);
        await process.WaitOrTerminateAsync(token);
        return GameStartOutcome.Exited;
    }

    /// <inheritdoc />
    public string? AfterSessionMessage(GameStartRequest request, GameStartOutcome outcome) => null;
}

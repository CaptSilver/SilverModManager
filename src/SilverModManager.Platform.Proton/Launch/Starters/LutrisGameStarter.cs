using CliWrap;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Starters;
using SilverModManager.Platform.Proton.ToolRunner.Dependencies;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch.Starters;

/// <summary>
/// Starts a game through <c>lutris lutris:rungame/&lt;slug&gt;</c>. Lutris stays open after the game, so the
/// command's own exit means nothing: the game counts as started when its processes show up and as over when
/// they have been gone for the settle period.
/// </summary>
public sealed class LutrisGameStarter(IExecutableLocator executables, LutrisFlatpakDependency flatpak, IProcessRunner runner, IRunningGameDetector detector, ILogger<LutrisGameStarter> logger) : IGameStarter
{
    private const string NotFound = "Lutris was not found: neither a lutris binary on PATH nor the Flatpak net.lutris.Lutris. Install it or start the game another way.";

    /// <inheritdoc />
    public GameStarterKind Kind => GameStarterKind.Lutris;

    /// <inheritdoc />
    /// <remarks>The <c>lutris:rungame</c> URL has no documented way to pass arguments.</remarks>
    public bool CanCarryArguments(GameStartRequest request) => request.AllArgs.Length == 0;

    /// <inheritdoc />
    public void CheckCanStart(GameStartRequest request)
    {
        if (Locate(request) is (ClientBinary.None, _))
            throw new GameLaunchException(NotFound);
    }

    private (ClientBinary Binary, AbsolutePath? Native) Locate(GameStartRequest request)
    {
        var client = ClientLaunchWait.ClientOf(request, StoreClientKind.Lutris);
        var native = executables.Find("lutris");
        return (ClientLaunchWait.Choose(client.IsFlatpak, native is not null, flatpak.IsInstalled), native);
    }

    /// <inheritdoc />
    public async Task<GameStartOutcome> Start(GameStartRequest request, IGameLaunch launch, CancellationToken token)
    {
        var client = ClientLaunchWait.ClientOf(request, StoreClientKind.Lutris);
        var url = $"lutris:rungame/{client.AppName}";
        var command = Locate(request) switch
        {
            (ClientBinary.Flatpak, _) => Cli.Wrap("flatpak").WithArguments(["run", LutrisFlatpakDependency.FlatpakId, url]),
            (ClientBinary.Native, { } native) => Cli.Wrap(native.ToString()).WithArguments([url]),
            _ => throw new GameLaunchException(NotFound),
        };

        logger.LogInformation("Starting {Game} through Lutris: `{Command}`", request.GameName, command.ToString());
        // Not awaited: Lutris keeps running after the game.
        runner.Run(command.WithValidation(CommandResultValidation.None), logOutput: true);
        return await ClientLaunchWait.UntilStartedThenGone(request, launch, detector, () => false, logger, "Lutris", token);
    }

    /// <inheritdoc />
    public string? AfterSessionMessage(GameStartRequest request, GameStartOutcome outcome)
        => outcome == GameStartOutcome.TimedOut
            ? $"Lutris did not start {request.GameName} within {SteamGameStarter.DescribeTimeout(request.Timing.StartTimeout)}. Make sure Lutris is installed and the game is in its library, then press Play again."
            : null;
}

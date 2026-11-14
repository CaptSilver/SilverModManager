using System.Diagnostics;
using CliWrap;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Starters;

/// <summary>
/// Runs the program itself: through the tool runner when it is a Windows program, through the shell when the
/// tool asks for its own console, and as a plain process otherwise. A launcher or loader may exit before the
/// game it starts, so the wait is over when the installation's processes have been gone for the settle period.
/// </summary>
public sealed class DirectGameStarter(IToolRunner toolRunner, IProcessRunner processRunner, IPrefixResolver prefixResolver, IRunningGameDetector detector, ILogger<DirectGameStarter> logger) : IGameStarter
{
    /// <inheritdoc />
    public GameStarterKind Kind => GameStarterKind.Direct;

    /// <inheritdoc />
    public bool CanCarryArguments(GameStartRequest request) => true;

    /// <summary>
    /// A script extender or launcher of a Windows build is a Windows program, and Linux cannot start it
    /// directly. Asked before the session so the loadout is not applied for a program that cannot run.
    /// </summary>
    public void CheckCanStart(GameStartRequest request)
    {
        if (!ViaToolRunner(request) || toolRunner.CanRun(request.Installation, out var reason, out var x86_64)) return;

        var message = $"Cannot run {request.Spec.Exe.FileName} for {request.GameName}: {reason}";
        throw x86_64 ? new X86_64HostRequiredException(message) : new GameLaunchException(message);
    }

    private bool ViaToolRunner(GameStartRequest request)
        => LaunchPlatform.IsWindowsProgram(prefixResolver.Resolve(request.Installation), request.Installation.LocatorResult);

    /// <inheritdoc />
    public async Task<GameStartOutcome> Start(GameStartRequest request, IGameLaunch launch, CancellationToken token)
    {
        var program = request.Spec.Exe;
        var arguments = request.AllArgs;

        if (ViaToolRunner(request))
        {
            // The shell would hand a Windows program to the host, which cannot open it, so this wins over UseShell.
            logger.LogInformation("Running {Program} through the tool runner", program);
            var result = await toolRunner.RunWindowsTool(request.Installation, program, arguments, null, logOutput: true, token, purpose: ToolRunPurpose.Game);
            if (result.ExitCode != 0)
                logger.LogWarning("Application closed with a non-zero exit code ({ExitCode}): {Application}", result.ExitCode, program);
        }
        else if (request.UseShell)
        {
            logger.LogInformation("Running {Program} through shell", program);
            await RunWithShell(program, arguments, token);
        }
        else
        {
            await RunCommand(program, arguments, token);
        }

        // The program may have been only a loader: the game is whatever the process table shows for the
        // installation, and it is over once nothing has shown up for the settle period.
        logger.LogInformation("Waiting for the game's processes to exit");
        await detector.WaitUntilGone(request.Installation, new GoneWait(request.Timing.Settle, Interval: request.Timing.PollInterval, Progress: OpenToolsProgress.Of(launch)), token);
        logger.LogInformation("Finished running {Program}", program);
        return GameStartOutcome.Exited;
    }

    /// <inheritdoc />
    public string? AfterSessionMessage(GameStartRequest request, GameStartOutcome outcome) => null;

    private async Task RunCommand(AbsolutePath program, string[] arguments, CancellationToken token)
    {
        var command = new Command(program.ToString())
            .WithArguments(arguments)
            .WithWorkingDirectory(program.Parent.ToString());

        await processRunner.RunAsync(command, cancellationToken: token);
    }

    private async Task RunWithShell(AbsolutePath program, string[] arguments, CancellationToken token)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = program.ToString(),
                WorkingDirectory = program.Parent.ToString(),
                // ArgumentList is refused with UseShellExecute, so the quoted string is the only way in. Linux
                // splits it into argv itself and never hands it to sh, so metacharacters stay literal.
                Arguments = new CliWrap.Builders.ArgumentsBuilder().Add(arguments).Build(),
                UseShellExecute = true,
                CreateNoWindow = false,
            },
            EnableRaisingEvents = true,
        };

        try
        {
            await processRunner.RunAsync(process, token);
        }
        catch (Exception e)
        {
            if (!token.IsCancellationRequested)
                logger.LogError(e, "While Running {Filename}", program);
        }

        if (process.ExitCode != 0)
            logger.LogWarning("Application closed with a non-zero exit code ({ExitCode}): {Application}", process.ExitCode, program);
    }
}

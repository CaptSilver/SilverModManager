using CliWrap;
using SilverModManager.Abstractions.Loadouts;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using R3;

namespace SilverModManager.Platform.Proton.UserTools;

/// <summary>
/// A program the user added, as a tool, so <see cref="IToolManager.RunTool"/> synchronizes the loadout before
/// and after it like it does for any other tool.
/// </summary>
public sealed class UserToolAdapter(
    GameId gameId,
    string name,
    AbsolutePath exe,
    string arguments,
    ToolRunVia via,
    IToolRunner toolRunner,
    IProcessRunner processRunner) : ITool
{
    public AbsolutePath Exe { get; } = exe;

    public ToolRunVia Via { get; } = via;

    public IEnumerable<GameId> GameIds => [gameId];

    public string Name => name;

    public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
    {
        return monitor.Begin<ITool, Unit>(this, async _ =>
        {
            await Execute(loadout.InstallationInstance, cancellationToken);
            return Unit.Default;
        });
    }

    internal async Task Execute(GameInstallation installation, CancellationToken cancellationToken)
    {
        var args = ArgumentSplitter.Split(arguments);

        if (Via is ToolRunVia.Native)
        {
            var command = Cli.Wrap(Exe.ToString())
                .WithArguments(args)
                .WithWorkingDirectory(Exe.Parent.ToString())
                .WithValidation(CommandResultValidation.None);
            await processRunner.RunAsync(command, logOutput: true, cancellationToken);
            return;
        }

        // The runner reports a non-zero exit instead of throwing; editors exit non-zero for ordinary reasons
        // (cancelled save, closed with unsaved changes), so it is not an error here.
        await toolRunner.RunWindowsTool(installation, Exe, args, null, logOutput: true, cancellationToken, Via);
    }
}

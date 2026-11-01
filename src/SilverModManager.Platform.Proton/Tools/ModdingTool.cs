using CliWrap;
using NexusMods.Paths;
using R3;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>
/// Runs one program from a <see cref="ToolDefinition"/>, as the user has it installed. The loadout is
/// synchronized around the run by <see cref="IToolManager.RunTool"/>, so what the program writes into the game
/// folder is ingested like for any other tool. Windows programs go through <see cref="IToolRunner"/>, which
/// turns host paths into Wine ones; native programs are started directly.
/// </summary>
public sealed class ModdingTool(
    ToolDefinition definition,
    InstalledTool installed,
    GameId gameId,
    IToolRunner toolRunner,
    IProcessRunner processRunner,
    ToolArgumentResolver resolver) : ITool
{
    public AbsolutePath Exe => installed.Exe;

    public IEnumerable<GameId> GameIds => [gameId];

    public string Name => definition.DisplayName;

    public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
    {
        return monitor.Begin<ITool, Unit>(this, async _ =>
        {
            await Execute(loadout, cancellationToken);
            return Unit.Default;
        });
    }

    internal async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var args = Arguments(loadout);
        var via = installed.RunVia ?? definition.RunVia;
        var exitCode = via is ToolRunVia.Native
            ? await RunNative(args, cancellationToken)
            : await RunWindows(loadout.InstallationInstance, args, via, cancellationToken);

        if (definition.ExitCodes is ExitCodePolicy.ZeroIsSuccess && exitCode != 0)
            throw new InvalidOperationException($"{Name} exited with code {exitCode}; see the process log.");
    }

    // A line the user typed replaces the definition's words. Host paths go in as they are: the tool runner
    // translates the arguments of a Windows exe, and a second pass here would translate twice.
    private IReadOnlyList<string> Arguments(Loadout.ReadOnly loadout)
    {
        var extra = new Dictionary<string, string> { [ToolVariableNames.Exe] = Exe.GetFullPath() };
        if (installed.Arguments is { } line)
            return resolver.ResolveLine(line, loadout, extra);

        var words = resolver.Resolve(definition.Arguments, loadout, extra);
        var game = loadout.InstallationInstance.GetGame();
        return definition.ExtraArguments is { } more ? [.. words, .. more(game, Exe.FileName)] : words;
    }

    private async Task<int> RunWindows(GameInstallation installation, IReadOnlyList<string> args, ToolRunVia via, CancellationToken cancellationToken)
    {
        // Without a prefix to run in, starting quietly would leave the user wondering why nothing happened.
        if (!toolRunner.CanRun(installation, out var cannotRunReason))
            throw new InvalidOperationException($"Cannot run {Name} for {installation.Game.DisplayName}: {cannotRunReason}");

        var result = await toolRunner.RunWindowsTool(installation, Exe, args, null, logOutput: true, cancellationToken, via);
        return result.ExitCode;
    }

    private async Task<int> RunNative(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var command = Cli.Wrap(Exe.ToString())
            .WithArguments(args)
            .WithWorkingDirectory(Exe.Parent.ToString())
            .WithValidation(CommandResultValidation.None);
        var result = await processRunner.RunAsync(command, logOutput: true, cancellationToken);
        return result.ExitCode;
    }
}

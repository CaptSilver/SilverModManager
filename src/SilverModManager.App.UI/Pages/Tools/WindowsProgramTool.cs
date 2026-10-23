using SilverModManager.Abstractions.Loadouts;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using R3;

namespace SilverModManager.App.UI.Pages.Tools;

/// <summary>
/// A program the user picked, wrapped as a tool so <see cref="IToolManager.RunTool"/> synchronizes the
/// loadout before and after it. Whatever the program writes into the game folder (xEdit's plugins, for
/// example) is ingested the same way as for any other tool.
/// </summary>
internal sealed class WindowsProgramTool(IToolRunner toolRunner, GameId gameId, AbsolutePath exe) : ITool
{
    public AbsolutePath Exe { get; } = exe;

    public IEnumerable<GameId> GameIds => [gameId];

    public string Name => Exe.FileName;

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
        // The runner reports a non-zero exit instead of throwing; editors exit non-zero for ordinary reasons
        // (cancelled save, closed with unsaved changes), so it is not an error here.
        await toolRunner.RunWindowsTool(loadout.InstallationInstance, Exe, [], null, logOutput: true, cancellationToken);
    }
}

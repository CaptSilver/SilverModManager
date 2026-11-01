using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel;

/// <summary>
/// Default implementation of <see cref="IToolManager"/>.
/// </summary>
public class ToolManager : IToolManager
{
    private readonly ILookup<GameId, ITool> _tools;
    private readonly IToolProvider[] _providers;
    private readonly ILogger<ToolManager> _logger;
    private readonly ISynchronizerService _syncService;
    private readonly ToolOutputCapture _outputCapture;

    /// <summary>
    /// DI Constructor
    /// </summary>
    public ToolManager(ILogger<ToolManager> logger, IEnumerable<ITool> tools, IEnumerable<IToolProvider> providers, ISynchronizerService syncService, ToolOutputCapture outputCapture)
    {
        _logger = logger;
        _tools = tools.SelectMany(tool => tool.GameIds.Select(gameId => (gameId, tool)))
            .ToLookup(t => t.gameId, t => t.tool);
        _providers = providers.ToArray();
        _syncService = syncService;
        _outputCapture = outputCapture;
    }

    /// <inheritdoc />
    public IEnumerable<ITool> GetTools(Loadout.ReadOnly loadout)
    {
        return _tools[loadout.Game.GameId].Concat(_providers.SelectMany(p => p.GetTools(loadout)));
    }

    private static string? GameFolderGroup(ITool tool) => tool is IToolWithOutput { Output: ToolOutput.GameFolder output }
        ? output.GroupName
        : null;

    private static string? OutputGroupName(ITool tool) => tool is IToolWithOutput { Output: var output }
        ? output switch
        {
            ToolOutput.GameFolder gameFolder => gameFolder.GroupName,
            ToolOutput.DeclaredFolder declared => declared.GroupName,
            _ => null,
        }
        : null;

    private static string? OwnGroupToDisable(ITool tool) => tool is IToolWithOutput { DisableOwnOutputDuringRun: true, Output: ToolOutput.DeclaredFolder output }
        ? output.GroupName
        : null;

    /// <inheritdoc />
    public async Task<Loadout.ReadOnly> RunTool(
        ITool tool,
        Loadout.ReadOnly loadout,
        IJobMonitor monitor,
        CancellationToken token = default)
        => (await RunToolWithOutcome(tool, loadout, monitor, token)).Loadout;

    /// <inheritdoc />
    public async Task<ToolRunOutcome> RunToolWithOutcome(
        ITool tool,
        Loadout.ReadOnly loadout,
        IJobMonitor monitor,
        CancellationToken token = default)
    {
        if (!tool.GameIds.Contains(loadout.Game.GameId)) throw new NotSupportedException("Tool does not support this game");

        // A Play is up to three syncs (the apply, the launch session's own before the process and its ingest after it),
        // and the player is asked at most once: the scope carries the first answer to the others.
        using var answer = BackupOverflowAnswer.Begin(tool is IRunGameTool ? BackupOverflowOperation.Launch : BackupOverflowOperation.Apply);

        // A tool that regenerates all of its output must not read last run's: the group goes off before the apply
        // takes it from disk. A run that fails turns it back on, and one that succeeds has replaced it already.
        var isGame = tool is IRunGameTool;
        string? disabledGroup = null;
        if (OwnGroupToDisable(tool) is { } ownGroup && await _outputCapture.SetEnabled(loadout.LoadoutId, ownGroup, enabled: false))
            disabledGroup = ownGroup;

        Loadout.ReadOnly appliedLoadout;
        OverridesSnapshot? overridesBefore = null;
        try
        {
            _logger.LogInformation("Applying loadout {LoadoutId} on {GameName} {GameVersion}", 
                loadout.Id, loadout.InstallationInstance.Game.DisplayName, loadout.GameVersion);
            using (SyncOrigin.Begin(isGame ? SyncTrigger.Launch : SyncTrigger.ToolRun))
                await _syncService.Synchronize(loadout);
            appliedLoadout = loadout.Rebase();

            // Taken after the apply: the apply ingests what the user changed before pressing Run into Overrides, and
            // that is not the tool's doing.
            if (GameFolderGroup(tool) is not null) overridesBefore = _outputCapture.SnapshotOverrides(appliedLoadout);

            _logger.LogInformation("Running tool {ToolName} for loadout {LoadoutId} on {GameName} {GameVersion}", 
                tool.Name, appliedLoadout.Id, appliedLoadout.InstallationInstance.Game.DisplayName, appliedLoadout.GameVersion);
            await tool.StartJob(appliedLoadout, monitor, token);
        }
        catch
        {
            if (disabledGroup is not null) await _outputCapture.SetEnabled(loadout.LoadoutId, disabledGroup, enabled: true);
            throw;
        }

        _logger.LogInformation("Ingesting loadout {LoadoutId} from {GameName} {GameVersion}", 
            appliedLoadout.Id, appliedLoadout.InstallationInstance.Game.DisplayName, appliedLoadout.GameVersion);
        using (SyncOrigin.Begin(isGame ? SyncTrigger.AfterGameRun : SyncTrigger.ToolRun))
            await _syncService.Synchronize(appliedLoadout);

        // What reached Overrides during the run is the tool's output. It belongs in the tool's group, which ranks
        // above the mods; left in Overrides it would be mixed with everything else the user changed by hand.
        var moved = 0;
        if (overridesBefore is not null && GameFolderGroup(tool) is { } groupName && tool is IToolWithOutput withOutput)
        {
            var ingested = appliedLoadout.Rebase();
            moved = await _outputCapture.MoveOverridesDelta(ingested, overridesBefore, _outputCapture.SnapshotOverrides(ingested), groupName, withOutput.IgnoredOutput);
        }

        var summary = (tool as IToolWithRunSummary)?.LastRun;
        return new ToolRunOutcome(appliedLoadout.Rebase(), OutputGroupName(tool), summary?.ExitCode, moved + (summary?.CapturedFiles ?? 0));
    }
}

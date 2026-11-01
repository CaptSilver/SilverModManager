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

    private static string? OwnGroupToDisable(ITool tool) => tool is IToolWithOutput { DisableOwnOutputDuringRun: true, Output: ToolOutput.DeclaredFolder output }
        ? output.GroupName
        : null;

    /// <inheritdoc />
    public async Task<Loadout.ReadOnly> RunTool(
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
        try
        {
            _logger.LogInformation("Applying loadout {LoadoutId} on {GameName} {GameVersion}", 
                loadout.Id, loadout.InstallationInstance.Game.DisplayName, loadout.GameVersion);
            using (SyncOrigin.Begin(isGame ? SyncTrigger.Launch : SyncTrigger.ToolRun))
                await _syncService.Synchronize(loadout);
            appliedLoadout = loadout.Rebase();

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

        return appliedLoadout.Rebase();
    }
}

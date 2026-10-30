using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
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

    /// <summary>
    /// DI Constructor
    /// </summary>
    public ToolManager(ILogger<ToolManager> logger, IEnumerable<ITool> tools, IEnumerable<IToolProvider> providers, ISynchronizerService syncService)
    {
        _logger = logger;
        _tools = tools.SelectMany(tool => tool.GameIds.Select(gameId => (gameId, tool)))
            .ToLookup(t => t.gameId, t => t.tool);
        _providers = providers.ToArray();
        _syncService = syncService;
    }

    /// <inheritdoc />
    public IEnumerable<ITool> GetTools(Loadout.ReadOnly loadout)
    {
        return _tools[loadout.Game.GameId].Concat(_providers.SelectMany(p => p.GetTools(loadout)));
    }

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

        _logger.LogInformation("Applying loadout {LoadoutId} on {GameName} {GameVersion}", 
            loadout.Id, loadout.InstallationInstance.Game.DisplayName, loadout.GameVersion);
        var isGame = tool is IRunGameTool;
        using (SyncOrigin.Begin(isGame ? SyncTrigger.Launch : SyncTrigger.ToolRun))
            await _syncService.Synchronize(loadout);
        var appliedLoadout = loadout.Rebase();

        _logger.LogInformation("Running tool {ToolName} for loadout {LoadoutId} on {GameName} {GameVersion}", 
            tool.Name, appliedLoadout.Id, appliedLoadout.InstallationInstance.Game.DisplayName, appliedLoadout.GameVersion);
        await tool.StartJob(appliedLoadout, monitor, token);

        _logger.LogInformation("Ingesting loadout {LoadoutId} from {GameName} {GameVersion}", 
            appliedLoadout.Id, appliedLoadout.InstallationInstance.Game.DisplayName, appliedLoadout.GameVersion);
        using (SyncOrigin.Begin(isGame ? SyncTrigger.AfterGameRun : SyncTrigger.ToolRun))
            await _syncService.Synchronize(appliedLoadout);

        return appliedLoadout.Rebase();
    }
}

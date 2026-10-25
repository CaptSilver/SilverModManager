using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.DataModel;

/// <summary>
/// Gives an install row the Nexus id of its game when the row was written before the game had one. The id is
/// otherwise stored only when the row is created, so a game that got its Nexus id in a later release kept
/// installs the Nexus pages treated as not being on Nexus Mods.
/// </summary>
/// <remarks>
/// Rows are matched to their game by the game id they carry, not by locating installs: a row on a library that
/// is not mounted at this start still gets its id, and no locator has to run before startup can go on.
/// Only fills a missing id. A row whose id differs from the game's is left alone: the row may have been keyed
/// by a sibling game that shares a Nexus id, and nothing here can tell a stale id from a deliberate one.
/// </remarks>
internal sealed class InstallNexusIdBackfill : IHostedService
{
    private readonly IConnection _connection;
    private readonly IEnumerable<IGameData> _games;
    private readonly ILogger<InstallNexusIdBackfill> _logger;

    public InstallNexusIdBackfill(IConnection connection, IEnumerable<IGameData> games, ILogger<InstallNexusIdBackfill> logger)
    {
        _connection = connection;
        _games = games;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Run();
        }
        catch (Exception e)
        {
            // Not worth failing startup over: the pass runs again on the next start.
            _logger.LogError(e, "Could not record the Nexus ids of installed games");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Fills in the id of every install row that lacks one and whose game declares one, in one transaction.
    /// Returns how many rows changed.
    /// </summary>
    internal async Task<int> Run()
    {
        var nexusIds = new Dictionary<GameId, (IGameData Game, NexusModsGameId Id)>();
        foreach (var game in _games)
        {
            if (game.NexusModsGameId.HasValue) nexusIds.TryAdd(game.GameId, (game, game.NexusModsGameId.Value));
        }

        var fixes = new List<(GameInstallMetadata.ReadOnly Row, IGameData Game, NexusModsGameId Id)>();
        foreach (var row in GameInstallMetadata.All(_connection.Db))
        {
            if (GameInstallMetadata.GameId.Contains(row)) continue;
            if (!GameInstallMetadata.Game.TryGetValue(row, out var gameId)) continue;
            if (!nexusIds.TryGetValue(gameId, out var known)) continue;
            fixes.Add((row, known.Game, known.Id));
        }

        if (fixes.Count == 0) return 0;

        using var tx = _connection.BeginTransaction();
        foreach (var (row, _, id) in fixes) tx.Add(row.Id, GameInstallMetadata.GameId, id);
        await tx.Commit();

        foreach (var (row, game, id) in fixes)
            _logger.LogInformation("Recorded Nexus Mods game id {NexusModsGameId} on the install of {Game} at `{Path}`", id, game.DisplayName, row.Path);
        return fixes.Count;
    }
}

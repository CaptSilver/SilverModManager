using Microsoft.Extensions.Logging;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.DataModel.SchemaVersions;

/// <summary>
/// Gives every installation row written before installations carried a game id of their own that id.
/// </summary>
/// <remarks>
/// Every such row belongs to one of the games that were coded into the app then, so the answer is a fixed
/// table from Nexus id to game id. It deliberately asks no game registry: with Skyrim VR and Fallout 4 VR
/// defined, 1704 and 1151 name two games each, and a Skyrim SE row on a library that is not mounted at
/// this start would be left unkeyed.
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class _0001_KeyInstallsByGameId(ILogger<_0001_KeyInstallsByGameId> logger) : ITransactionalMigration
{
    public static (MigrationId Id, string Name) IdAndName => MigrationId.ParseNameAndId(nameof(_0001_KeyInstallsByGameId));

    /// <summary>
    /// Nexus id to game id as the coded games spelled them when this migration was written. Frozen: renaming
    /// a game later must not change what these rows mean.
    /// </summary>
    private static readonly Dictionary<uint, string> GameIds = new()
    {
        [1704] = "CreationEngine.SkyrimSE",
        [1151] = "CreationEngine.Fallout4",
        [3474] = "Larian.BaldursGate3",
        [3174] = "Bannerlord",
        [3333] = "RedEngine.Cyberpunk2077",
        [1303] = "StardewValley",
    };

    public Task Prepare(IDb db) => Task.CompletedTask;

    public void Migrate(ITransaction tx, IDb db)
    {
        foreach (var row in GameInstallMetadata.All(db))
        {
            if (GameInstallMetadata.Game.Contains(row)) continue;

            if (GameInstallMetadata.GameId.TryGetValue(row, out var nexusId) && GameIds.TryGetValue(nexusId.Value, out var gameId))
            {
                tx.Add(row.Id, GameInstallMetadata.Game, GameId.From(gameId));
                continue;
            }

            logger.LogWarning("Installation `{Path}` ({Store}, Nexus id {NexusId}) is not one of the games this migration knows and stays without a game id", row.Path, row.Store, nexusId);
        }
    }
}

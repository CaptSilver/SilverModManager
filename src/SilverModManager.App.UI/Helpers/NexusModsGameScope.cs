using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.App.UI.Helpers;

/// <summary>
/// What the UI does with a game's Nexus id, for games that may not have one. A game without a Nexus id has no
/// page, collections or downloads on Nexus Mods, and never matches a Nexus id coming from the outside.
/// </summary>
internal static class NexusModsGameScope
{
    public static bool IsOnNexusMods(this IGameData game) => game.NexusModsGameId.HasValue;

    /// <summary>
    /// Whether this id is <paramref name="candidate"/>. A missing id is no one's.
    /// </summary>
    public static bool IsNexusGame(this Optional<NexusModsGameId> gameId, NexusModsGameId candidate)
        => gameId.HasValue && gameId.Value == candidate;

    public static CollectionMetadata.ReadOnly[] CollectionsOf(this IGameData game, IDb db)
        => game.NexusModsGameId.HasValue ? CollectionDownloader.GetCollections(db, game.NexusModsGameId.Value) : [];

    /// <summary>
    /// Picks the game an nxm link belongs to. Several games can share a Nexus id (Skyrim SE and Skyrim VR), so
    /// one that has a loadout wins over one that has not, and when that still leaves several the first is taken
    /// and the choice logged.
    /// </summary>
    public static Optional<IGameData> ResolveNxmGame(
        IEnumerable<IGameData> games,
        NexusModsGameId nexusModsGameId,
        Func<GameId, bool> hasLoadout,
        ILogger logger)
    {
        var candidates = games.Where(game => game.NexusModsGameId.IsNexusGame(nexusModsGameId)).ToArray();
        if (candidates.Length == 0) return Optional<IGameData>.None;

        var withLoadout = candidates.Where(game => hasLoadout(game.GameId)).ToArray();
        var pool = withLoadout.Length > 0 ? withLoadout : candidates;

        if (pool.Length > 1)
        {
            logger.LogInformation(
                "Nexus game id {NexusModsGameId} belongs to several games ({Games}), using {Game}",
                nexusModsGameId, string.Join(", ", pool.Select(game => game.GameId)), pool[0].GameId
            );
        }

        return Optional<IGameData>.Create(pool[0]);
    }
}

using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
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
    /// The Nexus id of the game an install row belongs to. A game this build knows decides by its class, the same
    /// as for the left menu entry, whether or not it is located right now. The id stored on the row is only used
    /// for a game this build does not know.
    /// </summary>
    public static NexusModsGameId? NexusIdOf(this GameInstallMetadata.ReadOnly row, IEnumerable<IGameData> games)
    {
        if (GameInstallMetadata.Game.TryGetValue(row, out var gameId)
            && games.FirstOrDefault(game => game.GameId == gameId) is { } known)
            return known.NexusModsGameId.HasValue ? known.NexusModsGameId.Value : null;

        return GameInstallMetadata.GameId.TryGetValue(row, out var stored) ? stored : null;
    }

    public static bool IsOnNexusMods(this GameInstallMetadata.ReadOnly row, IEnumerable<IGameData> games) => row.NexusIdOf(games).HasValue;

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

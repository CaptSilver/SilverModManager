using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Abstractions.NexusWebApi;

/// <summary>
/// The Nexus id of the game a loadout or install row belongs to, for games that may not have one.
/// </summary>
public static class NexusModsGameIds
{
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

    /// <summary>The Nexus id of the loadout's game, or null when there is no loadout yet or its game is not on Nexus Mods.</summary>
    public static NexusModsGameId? NexusIdOfLoadout(this IDb db, LoadoutId loadoutId, IEnumerable<IGameData> games)
        => loadoutId == default ? null : Loadout.Load(db, loadoutId).Installation.NexusIdOf(games);
}

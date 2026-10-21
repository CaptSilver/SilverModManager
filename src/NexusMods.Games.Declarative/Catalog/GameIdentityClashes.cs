using NexusMods.Sdk.Games;

namespace NexusMods.Games.Declarative.Catalog;

/// <summary>A game as far as identity goes: what names it and which store ids find it.</summary>
/// <param name="Label">What to call the game in a message, a file name or a type.</param>
public readonly record struct GameIdentity(string Label, GameId GameId, StoreIdentifiers Stores);

/// <summary>
/// Finds games that share a <see cref="GameId"/> or a store id. Every game locator keys a dictionary by
/// store id and throws on a repeat, so one repeated id would take the whole locator down.
/// </summary>
public static class GameIdentityClashes
{
    /// <summary>One message per id used by more than one game, naming the games. A Nexus id is not an identity here: it may repeat.</summary>
    public static IReadOnlyList<string> Find(IEnumerable<GameIdentity> games)
    {
        var all = games.ToArray();
        var clashes = new List<string>();

        foreach (var group in all.GroupBy(game => game.GameId).Where(group => group.Count() > 1))
            clashes.Add($"game id '{group.Key}' is used by {Labels(group)}");

        Add("Steam app id", game => game.Stores.SteamAppIds.Select(id => id.ToString()));
        Add("GOG product id", game => game.Stores.GOGProductIds.Select(id => id.ToString()));
        Add("Epic catalog item id", game => game.Stores.EGSCatalogItemId);
        Add("Amazon product id", game => game.Stores.AmazonProductIds);
        return clashes;

        void Add(string kind, Func<GameIdentity, IEnumerable<string>> idsOf)
        {
            var byId = all.SelectMany(game => idsOf(game).Select(id => (Id: id, Game: game))).GroupBy(pair => pair.Id);
            foreach (var group in byId.Where(group => group.Count() > 1))
                clashes.Add($"{kind} {group.Key} is used by {Labels(group.Select(pair => pair.Game))}");
        }

        static string Labels(IEnumerable<GameIdentity> group) => string.Join(" and ", group.Select(game => game.Label));
    }
}

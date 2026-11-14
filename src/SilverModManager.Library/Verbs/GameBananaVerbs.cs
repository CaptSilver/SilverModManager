using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Abstractions.Loadouts.Verbs;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Library.GameBanana;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Library.Verbs;

/// <summary>
/// The verbs of the GameBanana catalog: search a game's mods and install one by its id, naming the file when the mod has
/// several. They print what <c>catalog search</c> and <c>catalog install</c> print.
/// </summary>
internal static class GameBananaVerbs
{
    internal static IServiceCollection AddGameBananaVerbs(this IServiceCollection services) => services
        .AddVerb(() => Search)
        .AddVerb(() => Install)
        .AddModule("gamebanana", "Commands for the mods GameBanana lists for a game");

    [Verb("gamebanana search", "Lists one page of the loadout's game's mods on GameBanana, or the ones matching a query")]
    private static async Task<int> Search(
        [Injected] IRenderer renderer,
        [Injected] GameBananaCatalog catalog,
        [Injected] IEnumerable<IGameData> games,
        [Injected] IAdultContentSetting adultContent,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout whose game's mods to list")] Loadout.ReadOnly loadout,
        [Option("q", "query", "Text to search for; leave it out to list the game's front page", true)] string? query,
        [Option("o", "sort", "The sort's key, as the error for a wrong one lists them; the front page when left out", true)] string? sort,
        [Option("c", "cursor", "Where the previous page ended, as that run printed it", true)] string? cursor,
        [Option("a", "adult", "Include adult mods even when the app's Show adult content setting is off", true)] bool adult,
        [Option("j", "json", "Print the cards as JSON, then the cursor of the next page", true)] bool json)
        => await CatalogVerbs.RunSearch(renderer, catalog, CatalogVerbs.GameOf(games, loadout), adultContent, token, query, sort, cursor, adult, json);

    [Verb("gamebanana install", "Installs a mod's file from GameBanana into the loadout; a mod with several files needs --file")]
    private static async Task<int> Install(
        [Injected] IRenderer renderer,
        [Injected] GameBananaCatalog catalog,
        [Injected] IEnumerable<IGameData> games,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout to install into")] Loadout.ReadOnly loadout,
        [Option("m", "mod", "The mod's id, the number in its page address, as `gamebanana search` prints it")] long mod,
        [Option("f", "file", "The id of the file to install, when the mod has several", true)] long? file,
        [Option("j", "json", "Print the outcomes as JSON", true)] bool json)
    {
        var game = CatalogVerbs.GameOf(games, loadout);
        var card = await catalog.Find(game, mod.ToString(System.Globalization.CultureInfo.InvariantCulture), token)
            ?? throw new LoadoutVerbRefusedException($"GameBanana has no mod {mod} for {game.DisplayName}; `silvermodmanager gamebanana search` lists them");

        var chosen = file is { } id ? card with { Handle = new GameBananaHandle(mod, id) } : card;
        return await CatalogVerbs.RunInstall(renderer, catalog, chosen, loadout, json, token);
    }
}

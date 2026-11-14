using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts.Verbs;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Library.ModIo;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Library.Verbs;

/// <summary>
/// The verbs of the mod.io catalog: search a game's mods, install or subscribe to one by its id, and list the held mods
/// that have a newer file. They print what <c>catalog search</c> and <c>catalog install</c> print. Every one needs the
/// access token of the Settings → Accounts → mod.io row.
/// </summary>
internal static class ModIoVerbs
{
    // Nothing was installed and the user has something to do.
    private const int ExitNotInstalled = 2;

    internal static IServiceCollection AddModIoVerbs(this IServiceCollection services) => services
        .AddVerb(() => Search)
        .AddVerb(() => Install)
        .AddVerb(() => Updates)
        .AddModule("modio", "Commands for the mods mod.io lists for a game");

    [Verb("modio search", "Lists one page of the loadout's game's mods on mod.io, or the ones matching a query")]
    private static async Task<int> Search(
        [Injected] IRenderer renderer,
        [Injected] ModIoCatalog catalog,
        [Injected] IEnumerable<IGameData> games,
        [Injected] IAdultContentSetting adultContent,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout whose game's mods to list")] Loadout.ReadOnly loadout,
        [Option("q", "query", "Text to search for; leave it out to list the game's mods", true)] string? query,
        [Option("o", "sort", "The sort's key, as the error for a wrong one lists them; the most recently updated when left out", true)] string? sort,
        [Option("c", "cursor", "Where the previous page ended, as that run printed it", true)] string? cursor,
        [Option("a", "adult", "Include adult mods even when the app's Show adult content setting is off", true)] bool adult,
        [Option("j", "json", "Print the cards as JSON, then the cursor of the next page", true)] bool json)
        => await CatalogVerbs.RunSearch(renderer, catalog, CatalogVerbs.GameOf(games, loadout), adultContent, token, query, sort, cursor, adult, json);

    [Verb("modio install", "Installs a mod from mod.io into the loadout, or subscribes the account to it where the game's own mod manager downloads it")]
    private static async Task<int> Install(
        [Injected] IRenderer renderer,
        [Injected] ModIoCatalog catalog,
        [Injected] IEnumerable<IGameData> games,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout to install into")] Loadout.ReadOnly loadout,
        [Option("m", "mod", "The mod's id, as `modio search` prints it")] long mod,
        [Option("j", "json", "Print the outcomes as JSON", true)] bool json)
    {
        var game = CatalogVerbs.GameOf(games, loadout);
        if (mod is <= 0 or > uint.MaxValue) throw new LoadoutVerbRefusedException($"{mod} is not a mod.io mod id; it is the number `silvermodmanager modio search` prints");
        Abstractions.Library.Catalog.CatalogCard? card;
        try
        {
            card = await catalog.Find(game, mod.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
        }
        catch (ModIoNotSignedInException e)
        {
            await renderer.TextLine(e.Message);
            return ExitNotInstalled;
        }

        return await CatalogVerbs.RunInstall(
            renderer, catalog,
            card ?? throw new LoadoutVerbRefusedException($"mod.io has no mod {mod} for {game.DisplayName}; `silvermodmanager modio search` lists them"),
            loadout, json, token);
    }

    [Verb("modio updates", "Lists the held mod.io mods that have a newer file, asking mod.io again")]
    private static async Task<int> Updates(
        [Injected] IRenderer renderer,
        [Injected] IEnumerable<IModSource> sources,
        [Injected] IEnumerable<IGameData> games,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout whose game's mods to check")] Loadout.ReadOnly loadout)
    {
        var game = CatalogVerbs.GameOf(games, loadout);
        if (ModIoGame.For(game) is null) throw new LoadoutVerbRefusedException($"{game.DisplayName} has no mods on mod.io.");
        var source = sources.OfType<ModIoModSource>().Single();

        await source.CheckUpdates(game, force: true, token);
        var updates = source.AllUpdates(game).OfType<ModIoModUpdate>().ToArray();
        if (updates.Length == 0)
        {
            await renderer.TextLine("No mod.io mod has an update.");
            return 0;
        }

        await renderer.Table(["Id", "Name", "Held", "Newest"], updates.Select(update => new object[] { update.ModId, update.Mod.Name, update.CurrentVersion, update.NewestVersion }));
        return 0;
    }
}

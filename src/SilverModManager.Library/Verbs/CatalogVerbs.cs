using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Abstractions.Loadouts.Verbs;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Library.Verbs;

/// <summary>
/// Search and install verbs over any registered <see cref="IModCatalog"/>, so a source that registers a catalog gets the
/// command line without writing verbs of its own.
/// </summary>
internal static class CatalogVerbs
{
    // Nothing was installed and the user has something to do: the source needs the website or refused the mod.
    private const int ExitNotInstalled = 2;

    internal static IServiceCollection AddCatalogVerbs(this IServiceCollection services) => services
        .AddVerb(() => Search)
        .AddVerb(() => Install)
        .AddModule("catalog", "Commands for browsing and installing from the mod sources of a game");

    private sealed record CardRow(string Id, string Title, string Author, string Version, long Downloads, double? Rating, string? Page);

    [Verb("catalog search", "Lists one page of a mod source's catalog for the loadout's game")]
    private static async Task<int> Search(
        [Injected] IRenderer renderer,
        [Injected] ModCatalogs catalogs,
        [Injected] IEnumerable<IGameData> games,
        [Injected] IAdultContentSetting adultContent,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout whose game's catalog to search")] Loadout.ReadOnly loadout,
        [Option("s", "source", "The mod source, such as Thunderstore")] string source,
        [Option("q", "query", "Text the mod name or author contains; leave it out to list everything", true)] string? query,
        [Option("o", "sort", "The sort's key, as the source lists them in its error when it is wrong; the first of its sorts when left out", true)] string? sort,
        [Option("c", "cursor", "Where the previous page ended, as that run printed it", true)] string? cursor,
        [Option("a", "adult", "Include adult mods even when the app's Show adult content setting is off", true)] bool adult,
        [Option("j", "json", "Print the cards as JSON, then the cursor of the next page", true)] bool json)
    {
        var (catalog, game) = CatalogOrRefuse(catalogs, games, loadout, source);
        var chosen = sort is null
            ? catalog.Sorts[0]
            : catalog.Sorts.FirstOrDefault(candidate => candidate.Key.Equals(sort, StringComparison.OrdinalIgnoreCase))
              ?? throw new LoadoutVerbRefusedException($"{catalog.DisplayName} has no sort '{sort}'; use one of {string.Join(", ", catalog.Sorts.Select(s => s.Key))}");

        var text = (query ?? "").Trim();
        var page = await catalog.Search(game, new CatalogQuery(text, chosen, cursor, adult || adultContent.ShowAdultContent), token);

        if (json)
        {
            var rows = page.Cards.Select(card => new CardRow(card.Id, card.Title, card.Author, card.Version, card.Downloads, card.Rating, card.Page?.ToString()));
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            await renderer.TextLine(JsonSerializer.Serialize(new { nextCursor = page.NextCursor, total = page.Total, fromCache = page.FromCache }, new JsonSerializerOptions(CliJson.Options) { WriteIndented = false }));
            return 0;
        }

        if (page.Cards.Count == 0)
        {
            await renderer.TextLine(text.Length == 0
                ? $"{catalog.DisplayName} lists no mods for this game."
                : $"No mod in {catalog.DisplayName} matches '{text}'.");
            return 0;
        }

        if (page.FromCache) await renderer.TextLine($"[note] {catalog.DisplayName} did not answer; this is the list saved earlier.");
        await renderer.Table(["Id", "Name", "Author", "Version", "Downloads", "Rating"], page.Cards.Select(card => new object[]
        {
            card.Id,
            card.Title,
            card.Author,
            card.Version,
            card.Downloads,
            card.Rating is { } rating ? rating.ToString("0") : "-",
        }));
        if (page.NextCursor is { } next) await renderer.TextLine($"More: pass --cursor {next} for the next page.");
        return 0;
    }

    [Verb("catalog install", "Installs a mod of a mod source into the loadout")]
    private static async Task<int> Install(
        [Injected] IRenderer renderer,
        [Injected] ModCatalogs catalogs,
        [Injected] IEnumerable<IGameData> games,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout to install into")] Loadout.ReadOnly loadout,
        [Option("s", "source", "The mod source, such as Thunderstore")] string source,
        [Option("i", "id", "The mod's id, as `catalog search` prints it")] string id,
        [Option("j", "json", "Print the outcomes as JSON", true)] bool json)
    {
        var (catalog, game) = CatalogOrRefuse(catalogs, games, loadout, source);
        var card = await catalog.Find(game, id, token)
            ?? throw new LoadoutVerbRefusedException($"{catalog.DisplayName} has no mod with the id '{id}' for this game; `silvermodmanager catalog search` lists them");

        switch (await catalog.Install(card, loadout.LoadoutId, token))
        {
            case CatalogInstallOutcome.Installed installed:
                if (json) await renderer.TextLine(InstallOutcomeRenderer.Json(installed.PerPackage));
                else await InstallOutcomeRenderer.Print(renderer, installed.PerPackage);
                return 0;
            case CatalogInstallOutcome.Subscribed subscribed:
                await renderer.TextLine(subscribed.Note);
                return 0;
            case CatalogInstallOutcome.OpenedOnWebsite opened:
                await renderer.TextLine(json
                    ? JsonSerializer.Serialize(new { needsBrowser = true, url = opened.Page.ToString() }, CliJson.Options)
                    : $"{card.Title} has to be downloaded on the site: {opened.Page}");
                return ExitNotInstalled;
            case CatalogInstallOutcome.Refused refused:
                await renderer.TextLine(refused.Why.Message);
                return ExitNotInstalled;
            default:
                throw new InvalidOperationException("A catalog answered an install with an outcome this verb does not know.");
        }
    }

    private static (IModCatalog Catalog, IGameData Game) CatalogOrRefuse(ModCatalogs catalogs, IEnumerable<IGameData> games, Loadout.ReadOnly loadout, string source)
    {
        var game = GameInstallMetadata.Game.TryGetValue(loadout.Installation, out var gameId)
            ? games.FirstOrDefault(candidate => candidate.GameId == gameId)
            : null;
        if (game is null) throw new LoadoutVerbRefusedException("The loadout's game is not one this app knows.");

        var serving = catalogs.For(game);
        if (serving.FirstOrDefault(candidate => candidate.DisplayName.Equals(source, StringComparison.OrdinalIgnoreCase)) is { } catalog) return (catalog, game);

        throw new LoadoutVerbRefusedException(serving.Count == 0
            ? $"{game.DisplayName} has no mod source to browse."
            : $"No mod source called '{source}' serves {game.DisplayName}; the ones that do are {string.Join(", ", serving.Select(s => s.DisplayName))}");
    }
}

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Abstractions.Loadouts.Verbs;
using SilverModManager.Library.GitHub;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GitHub;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Library.Verbs;

/// <summary>
/// The verbs of a game's GitHub shelf: the loaders and tools whose releases the game lists, and the repositories the
/// user added.
/// </summary>
internal static class GitHubVerbs
{
    // Nothing was installed and the user has something to do: the release has no such asset or GitHub is out of requests.
    private const int ExitNotInstalled = 2;

    internal static IServiceCollection AddGitHubVerbs(this IServiceCollection services) => services
        .AddVerb(() => Shelf)
        .AddVerb(() => Install)
        .AddVerb(() => Add)
        .AddModule("github", "Commands for the loaders and tools whose releases a game lists on GitHub");

    private sealed record ShelfRow(string Repository, string Role, string? Latest, string? Asset, bool Declared);

    [Verb("github shelf", "Lists the repositories on the loadout's game's GitHub shelf with the tag of their newest release")]
    private static async Task<int> Shelf(
        [Injected] IRenderer renderer,
        [Injected] GitHubShelfEntries entries,
        [Injected] GitHubReleases releases,
        [Injected] IEnumerable<IGameData> games,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout whose game's shelf to list")] Loadout.ReadOnly loadout,
        [Option("j", "json", "Print the rows as JSON", true)] bool json)
    {
        var game = GameOf(games, loadout);
        var rows = new List<ShelfRow>();
        foreach (var entry in entries.For(game))
        {
            var release = await releases.Latest(entry, force: false, token);
            var asset = release is null ? null : ReleaseAssets.Pick(release, entry.AssetSuffix, entry.NameHint)?.Name;
            rows.Add(new ShelfRow(entry.Name, entry.Role, release?.TagName, asset, entry.Declared));
        }

        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        if (rows.Count == 0)
        {
            await renderer.TextLine($"{game.DisplayName} has nothing on its GitHub shelf; `silvermodmanager github add` puts a repository there.");
            return 0;
        }

        await renderer.Table(["Repository", "Role", "Latest", "Asset"], rows.Select(row => new object[] { row.Repository, row.Role, row.Latest ?? "-", row.Asset ?? "-" }));
        return 0;
    }

    [Verb("github install", "Installs the newest release asset of a GitHub repository into the loadout, putting the repository on the game's shelf if it is not there")]
    private static async Task<int> Install(
        [Injected] IRenderer renderer,
        [Injected] GitHubShelfEntries entries,
        [Injected] GitHubShelfCatalog catalog,
        [Injected] IEnumerable<IGameData> games,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout to install into")] Loadout.ReadOnly loadout,
        [Option("r", "repository", "owner/repository, or the address of its page on github.com")] string repository,
        [Option("a", "asset", "The ending of the release file's name, such as .7z; a repository on the shelf already has one", true)] string? asset,
        [Option("", "hint", "Text the right file's name contains, when several end the same way", true)] string? hint,
        [Option("j", "json", "Print the outcomes as JSON", true)] bool json)
    {
        var game = GameOf(games, loadout);
        if (!GitHubShelfEntries.TryParseRepository(repository, out var owner, out var name))
            throw new LoadoutVerbRefusedException($"'{repository.Trim()}' is not a GitHub repository; use owner/repository or the address of its page on github.com");

        var entry = entries.For(game).FirstOrDefault(candidate => candidate.Name.Equals($"{owner}/{name}", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            if (string.IsNullOrWhiteSpace(asset))
                throw new LoadoutVerbRefusedException($"{owner}/{name} is not on {game.DisplayName}'s shelf, so say which release file to take with --asset, the ending of its name such as .7z");

            entry = await entries.Add(game.GameId, repository, asset, hint, includePrereleases: false);
        }
        else if (!string.IsNullOrWhiteSpace(asset))
        {
            entry = entry with { AssetSuffix = asset.Trim(), NameHint = string.IsNullOrWhiteSpace(hint) ? entry.NameHint : hint.Trim() };
        }

        switch (await catalog.Install(entry, loadout.LoadoutId, token))
        {
            case CatalogInstallOutcome.Installed installed:
                if (json) await renderer.TextLine(InstallOutcomeRenderer.Json(installed.PerPackage));
                else await InstallOutcomeRenderer.Print(renderer, installed.PerPackage);
                return 0;
            case CatalogInstallOutcome.Refused refused:
                await renderer.TextLine(refused.Why.Message);
                return ExitNotInstalled;
            default:
                throw new InvalidOperationException("The GitHub shelf answered an install with an outcome this verb does not know.");
        }
    }

    [Verb("github add", "Puts a GitHub repository on the loadout's game's shelf, so its releases can be installed and checked for updates")]
    private static async Task<int> Add(
        [Injected] IRenderer renderer,
        [Injected] GitHubShelfEntries entries,
        [Injected] IEnumerable<IGameData> games,
        [Option("l", "loadout", "The loadout whose game's shelf to add to")] Loadout.ReadOnly loadout,
        [Option("r", "repository", "owner/repository, or the address of its page on github.com")] string repository,
        [Option("a", "asset", "The ending of the release file's name, such as .7z")] string asset,
        [Option("", "hint", "Text the right file's name contains, when several end the same way", true)] string? hint,
        [Option("", "prereleases", "Offer prereleases as updates", true)] bool prereleases)
    {
        var game = GameOf(games, loadout);
        var entry = await entries.Add(game.GameId, repository, asset, hint, prereleases);
        await renderer.TextLine($"Added {entry.Name} to the GitHub shelf of {game.DisplayName}. {GitHubShelfCatalog.AddWarning(entry)}");
        return 0;
    }

    private static IGameData GameOf(IEnumerable<IGameData> games, Loadout.ReadOnly loadout)
    {
        var game = GameInstallMetadata.Game.TryGetValue(loadout.Installation, out var gameId)
            ? games.FirstOrDefault(candidate => candidate.GameId == gameId)
            : null;
        return game ?? throw new LoadoutVerbRefusedException("The loadout's game is not one this app knows.");
    }
}

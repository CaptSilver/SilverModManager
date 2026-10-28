using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.DataModel.CommandLine.Verbs;

/// <summary>
/// Verbs that report on the games found on this machine.
/// </summary>
public static class GameVerbs
{
    public static IServiceCollection AddGameVerbs(this IServiceCollection collection) =>
        collection
            .AddModule("games", "Commands for the games found on this machine")
            .AddVerb(() => ListGames)
            .AddVerb(() => BuildCovered);

    private sealed record GameRow(string Store, string Name, string Path, string GameId);

    [Verb("games list", "Lists the games found on this machine with their store and install folder")]
    private static async Task<int> ListGames([Injected] IRenderer renderer,
        [Injected] IGameRegistry registry,
        [Option("j", "json", "Print the games as JSON", true)] bool json)
    {
        var rows = registry.LocateGameInstallations()
            .Select(g => new GameRow(g.LocatorResult.Store.Value, g.Game.DisplayName, g.LocatorResult.Path.ToString(), g.Game.GameId.ToString()))
            .ToArray();

        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        if (rows.Length == 0)
        {
            await renderer.TextLine("No games were found.");
            return 0;
        }

        await renderer.Table(["Store", "Name", "Path"], rows.Select(r => new object[] { r.Store, r.Name, r.Path }));
        return 0;
    }

    /// <summary>The located installations of the game <paramref name="gameId"/>, in the registry's order.</summary>
    internal static ImmutableArray<GameInstallation> Located(IGameRegistry registry, string gameId)
    {
        var id = GameId.From(gameId);
        return [..registry.LocateGameInstallations().Where(g => g.Game.GameId == id)];
    }

    internal static string NotLocatedMessage(string gameId)
        => $"No installation of {gameId} was found on this machine. `games list --json` prints the game ids that were found.";

    [Verb("games build-covered", "Says whether a located game's installed build is in the game file data, so the clean-folder prompt would offer a clean")]
    private static async Task<int> BuildCovered([Injected] IRenderer renderer,
        [Injected] IGameRegistry registry,
        [Injected] IFileHashesService fileHashes,
        [Option("g", "game", "The game id, as `games list --json` prints it")] string gameId)
    {
        var installations = Located(registry, gameId);
        if (installations.IsEmpty)
        {
            await renderer.TextLine(NotLocatedMessage(gameId));
            return 1;
        }

        var allCovered = true;
        foreach (var installation in installations)
        {
            var result = installation.LocatorResult;
            var covered = fileHashes.CoversInstalledBuild((result.Store, [..result.LocatorIds]));
            allCovered &= covered;
            var ids = result.LocatorIds.IsEmpty ? "none" : string.Join(", ", result.LocatorIds.Select(i => i.Value));
            await renderer.TextLine($"{(covered ? "true" : "false")}: {result.Store.Value} at {result.Path}, locator ids: {ids}");
        }

        return allCovered ? 0 : 1;
    }
}

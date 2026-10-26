using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.DataModel.CommandLine.Verbs;

/// <summary>
/// Verbs that report on the games found on this machine.
/// </summary>
public static class GameVerbs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IServiceCollection AddGameVerbs(this IServiceCollection collection) =>
        collection
            .AddModule("games", "Commands for the games found on this machine")
            .AddVerb(() => ListGames);

    private sealed record GameRow(string Store, string Name, string Path);

    [Verb("games list", "Lists the games found on this machine with their store and install folder")]
    private static async Task<int> ListGames([Injected] IRenderer renderer,
        [Injected] IGameRegistry registry,
        [Option("j", "json", "Print the games as JSON", true)] bool json)
    {
        var rows = registry.LocateGameInstallations()
            .Select(g => new GameRow(g.LocatorResult.Store.Value, g.Game.DisplayName, g.LocatorResult.Path.ToString()))
            .ToArray();

        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(rows, JsonOptions));
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
}

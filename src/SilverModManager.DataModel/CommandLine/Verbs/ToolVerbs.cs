using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.DataModel.CommandLine.Verbs;

public static class ToolVerbs
{
    public static IServiceCollection AddToolVerbs(this IServiceCollection collection) =>
        collection.AddVerb(() => ListTools);

    private sealed record ToolRow(string Name, string[] GameIds);

    [Verb("list-tools", "Lists all tools available")]
    private static async Task<int> ListTools([Injected] IRenderer renderer,
        [Injected] IEnumerable<ITool> tools,
        [Injected] CancellationToken token,
        [Option("j", "json", "Print the tools as JSON", true)] bool json)
    {
        if (json)
        {
            var rows = tools.Select(t => new ToolRow(t.Name, t.GameIds.Select(id => id.ToString()).ToArray()));
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        await renderer.Table(new[] { "Name", "Description" },
            tools.Select(t => new object[] { t.Name, string.Join(", ", t.GameIds) }));
        return 0;
    }
}

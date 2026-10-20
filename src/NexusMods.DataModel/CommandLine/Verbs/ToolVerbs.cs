using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Cli;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.DataModel.CommandLine.Verbs;

internal static class ToolVerbs
{
    internal static IServiceCollection AddToolVerbs(this IServiceCollection collection) =>
        collection.AddVerb(() => ListTools);

    [Verb("list-tools", "Lists all tools available")]
    private static async Task<int> ListTools([Injected] IRenderer renderer,
        [Injected] IEnumerable<ITool> tools,
        [Injected] CancellationToken token)
    {
        await renderer.Table(new[] { "Name", "Description" },
            tools.Select(t => new object[] { t.Name, string.Join(", ", t.GameIds) }));
        return 0;
    }
}

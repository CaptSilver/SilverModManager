using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Platform.Proton.UserTools;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Platform.Proton.Verbs;

/// <summary>Beside <c>list-tools</c>: the verbs that add and run modding tools.</summary>
internal static class ToolRunVerbs
{
    internal static IServiceCollection AddToolRunVerbs(this IServiceCollection services)
    {
        services.TryAddSingleton<ToolInstaller>();
        services.TryAddSingleton<ToolPipelineRunner>();
        services.TryAddSingleton<ToolCommands>();
        return services.AddVerb(() => AddTool).AddVerb(() => RunTool).AddVerb(() => RunPipeline);
    }

    [Verb("add-tool", "Adds the tools in a folder, or one program, to a loadout's game")]
    private static Task<int> AddTool(
        [Injected] IRenderer renderer,
        [Injected] ToolCommands command,
        [Option("l", "loadout", "The loadout whose game gets the tool")] Loadout.ReadOnly loadout,
        [Option("p", "path", "Absolute path of the tool's folder or program")] string path)
        => command.AddAsync(renderer, loadout, path);

    [Verb("run-tool", "Runs an added tool against a loadout and waits for it to finish")]
    private static Task<int> RunTool(
        [Injected] IRenderer renderer,
        [Injected] ToolCommands command,
        [Option("l", "loadout", "The loadout to run the tool against")] Loadout.ReadOnly loadout,
        [Option("t", "tool", "The tool's name or catalogue id, as list-tools shows it")] string tool,
        [Option("c", "clean-plugin", "Run Quick Auto Clean on this plugin file name instead of the tool's normal mode", true)] string? cleanPlugin,
        [Injected] CancellationToken token)
        => command.RunToolAsync(renderer, loadout, tool, cleanPlugin, token);

    [Verb("run-pipeline", "Runs a list of added tools one after the other, stopping at the first failure")]
    private static Task<int> RunPipeline(
        [Injected] IRenderer renderer,
        [Injected] ToolCommands command,
        [Option("l", "loadout", "The loadout to run the pipeline against")] Loadout.ReadOnly loadout,
        [Option("n", "name", "The pipeline's name, for example \"LOD generation\"")] string name,
        [Injected] CancellationToken token)
        => command.RunPipelineAsync(renderer, loadout, name, token);
}

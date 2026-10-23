using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Platform.Proton.Verbs;

internal static class DeploymentVerbs
{
    internal static IServiceCollection AddDeploymentVerbs(this IServiceCollection services)
    {
        services.TryAddSingleton<LoadoutResolver>();
        services.TryAddSingleton<DeploymentCommand>();
        return services.AddVerb(() => Deployment);
    }

    [Verb("deployment", "Moves a game's roots to another deployment backend, or to the one the selection chain picks now")]
    private static Task<int> Deployment(
        [Injected] IRenderer renderer,
        [Injected] DeploymentCommand command,
        [Option("g", "game", "Steam app id or game name")] string game,
        [Option("r", "root", "The root to move, as shown by `silvermod doctor`; every root when left out", true)] string? root,
        [Option("u", "use", "Deploy with this backend and keep it, whatever the chain picks later", true)] string? use,
        [Option("c", "recommended", "Move roots the chain picked a backend for to the chain's pick now; roots chosen with --use stay", true)] bool recommended,
        [Option("j", "json", "Print the result as JSON", true)] bool json)
        => command.RunAsync(renderer, game, root, use, recommended, json);
}

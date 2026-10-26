using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Platform.Proton.Verbs;

internal static class PrefixVerbs
{
    internal static IServiceCollection AddPrefixVerbs(this IServiceCollection services)
    {
        services.TryAddSingleton<LoadoutResolver>();
        services.TryAddSingleton<PrefixSetupCommand>();
        return services
            .AddModule("prefix", "Commands for the Wine prefixes of games")
            .AddVerb(() => PrefixSetup);
    }

    [Verb("prefix setup", "Prints what installing the Windows components a game needs into its Wine prefix would do, or does it with --write and --prefix")]
    private static Task<int> PrefixSetup(
        [Injected] IRenderer renderer,
        [Injected] PrefixSetupCommand command,
        [Option("l", "loadout", "The loadout whose game's prefix to set up, by name")] string loadout,
        [Option("g", "game", "Steam app id or game name, when more than one game has that loadout", true)] string? game,
        [Option("w", "write", "Change the prefix. Needs --prefix; the game and its Wine session must be closed", true)] bool write,
        [Option("p", "prefix", "The prefix path the dry run printed, to confirm that this is the prefix to change", true)] string? prefix,
        [Injected] CancellationToken token)
        => command.RunAsync(renderer, loadout, game, write, prefix, token);
}

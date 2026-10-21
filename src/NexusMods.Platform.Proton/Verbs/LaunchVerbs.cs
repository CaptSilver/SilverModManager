using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Cli;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Platform.Proton.Verbs;

internal static class LaunchVerbs
{
    internal static IServiceCollection AddLaunchVerbs(this IServiceCollection services)
    {
        services.TryAddSingleton<SteamLaunchOptionCommand>();
        return services.AddVerb(() => SteamLaunchOption);
    }

    [Verb("steam-launch-option", "Prints the Steam launch options line that starts a loadout, or writes it into Steam's config")]
    private static Task<int> SteamLaunchOption(
        [Injected] IRenderer renderer,
        [Injected] SteamLaunchOptionCommand command,
        [Option("l", "loadout", "The loadout to start, by name")] string loadout,
        [Option("g", "game", "Steam app id or game name, when more than one game has that loadout", true)] string? game,
        [Option("w", "write", "Store the line in Steam's localconfig.vdf (Steam must be closed)", true)] bool write,
        [Option("r", "replace-other-manager", "Replace another mod manager's launch options; the value is the exact current line, as printed by --write", true)] string? replaceOtherManager)
        => command.RunAsync(renderer, loadout, game, write, replaceOtherManager);
}

using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Cli;
using NexusMods.Platform.Proton.Doctor;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Games.FromSoft.Cleanup;

internal static class CleanupVerbs
{
    internal static IServiceCollection AddCleanupVerbs(this IServiceCollection services)
    {
        services.AddSingleton<CleanupService>();
        services.AddSingleton<CleanupCommand>();
        services.AddSingleton<IGameCleanupGuide, EldenRingCleanupGuide>();
        services.AddSingleton<IImmutableFlagProbe, IoctlImmutableFlagProbe>();
        return services.AddVerb(() => Cleanup)
            .AddModule("elden-ring", "Commands for the Elden Ring installation");
    }

    [Verb("elden-ring cleanup", "Turns an Elden Ring game folder left by another mod manager back into a plain Steam install")]
    private static Task<int> Cleanup(
        [Injected] IRenderer renderer,
        [Injected] CleanupCommand command,
        [Option("l", "loadout", "The loadout that keeps the mods' configuration files")] Loadout.ReadOnly loadout,
        [Option("p", "plan", "Print the inventory and the steps; change nothing", true)] bool plan,
        [Option("a", "apply", "Run the steps once the plan has been read", true)] bool apply,
        [Option("y", "yes", "Confirm the steps --apply printed", true)] bool yes)
        => command.RunAsync(renderer, loadout, plan, apply, yes);
}

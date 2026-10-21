using NexusMods.Abstractions.Cli;
using NexusMods.Games.FromSoft.Cleanup;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Games.FromSoft.EldenRing;

internal static class EldenRingCleanupVerb
{
    internal const string Name = "elden-ring cleanup";

    [Verb(Name, "Turns an Elden Ring game folder left by another mod manager back into a plain Steam install")]
    internal static Task<int> Cleanup(
        [Injected] IRenderer renderer,
        [Injected] CleanupCommand command,
        [Option("l", "loadout", "The loadout that keeps the mods' configuration files")] Loadout.ReadOnly loadout,
        [Option("p", "plan", "Print the inventory and the steps; change nothing", true)] bool plan,
        [Option("a", "apply", "Run the steps once the plan has been read", true)] bool apply,
        [Option("y", "yes", "Confirm the steps --apply printed", true)] bool yes)
        => command.RunAsync(renderer, loadout, plan, apply, yes);
}

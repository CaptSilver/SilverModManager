using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Generic.Loaders;

/// <summary>
/// Mods in the loadout and no loader to load them. Reported against the first such group; one diagnostic is enough to
/// send the player to wherever the loader comes from. The game says which loader it has and how to word the report.
/// </summary>
public abstract class AProxyLoaderMissingEmitter<TGame> : ILoadoutDiagnosticEmitter where TGame : class, IGame
{
    /// <summary>The loader of <paramref name="game"/>.</summary>
    protected abstract AProxyDllLoader LoaderOf(TGame game);

    /// <summary>The diagnostic for <paramref name="group"/>, a mod group the game's loader would have to load.</summary>
    protected abstract Diagnostic Report(TGame game, LoadoutItemGroup.ReadOnly group, Loadout.ReadOnly loadout);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (loadout.InstallationInstance.Game is not TGame game) yield break;

        var loader = LoaderOf(game);
        if (loader.IsInstalled(loadout)) yield break;

        var group = loader.FirstModGroup(loadout);
        if (group.HasValue is false) yield break;

        yield return Report(game, group.Value, loadout);
    }
}

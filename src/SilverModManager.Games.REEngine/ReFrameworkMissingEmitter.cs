using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.REEngine;

/// <summary>
/// Scripts or plugins in the loadout and no REFramework to load them. Reported against the first such group; one
/// diagnostic is enough to send the player to the release page.
/// </summary>
public class ReFrameworkMissingEmitter : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (loadout.InstallationInstance.Game is not IReEngineGame game) yield break;
        if (game.Loader.IsInstalled(loadout)) yield break;

        var group = game.Loader.FirstScriptOrPluginGroup(loadout);
        if (!group.HasValue) yield break;

        yield return Diagnostics.CreateReFrameworkMissing(Group: group.Value.ToReference(loadout), Release: game.Loader.Release);
    }
}

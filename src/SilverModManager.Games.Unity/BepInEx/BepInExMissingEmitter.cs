using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity.BepInEx;

/// <summary>
/// Plugins in the loadout and no loader pack to load them. Reported against the first plugin group; one diagnostic is
/// enough to send the player to Thunderstore for the pack.
/// </summary>
public class BepInExMissingEmitter : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (loadout.InstallationInstance.Game is not IUnityGame game) yield break;
        if (game.Loader.IsInstalled(loadout)) yield break;

        var plugin = game.Loader.FirstPluginGroup(loadout);
        if (!plugin.HasValue) yield break;

        yield return Diagnostics.CreateBepInExMissing(Group: plugin.Value.ToReference(loadout), Package: game.Loader.LoaderPackage);
    }
}

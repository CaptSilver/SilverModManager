using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Diagnostics.References;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Unity.BepInEx;

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

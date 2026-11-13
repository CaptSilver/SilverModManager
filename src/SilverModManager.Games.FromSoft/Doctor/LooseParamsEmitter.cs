using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Doctor;

/// <summary>
/// Says that the game's regulation is off for a loadout whose mods ship loose params. A loadout that also has a mod
/// for the regulation is refused at apply and never reaches here with both, so this reports the loose-params-only case.
/// </summary>
public class LooseParamsEmitter(LoadoutMergeSources sources) : IFromSoftDiagnosticEmitter
{
    // Reads the loadout's mods, not the sync tree, so it gives the same answer without one.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (loadout.InstallationInstance.Game is not IFromSoftGame { Definition.LooseParams: { } rule } game) yield break;

        var verdict = LooseParamsCheck.Check(rule, sources.Providers(loadout));
        if (verdict is null || verdict.RegulationMods.Count > 0) yield break;

        yield return FromSoftDiagnostics.CreateRegulationOffForLooseParams(game.DisplayName, rule.DisablesFile, string.Join(", ", verdict.LooseParamMods));
    }
}

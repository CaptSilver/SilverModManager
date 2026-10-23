using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Doctor;

/// <summary>Warns about each item-spawner DLL in the game folder.</summary>
public class SpawnerEmitter : AFromSoftCheckEmitter
{
    protected override IEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, IFromSoftGame game)
        => FromSoftChecks.GameFolder(FromSoftPaths.GameFolder(game.Layout, loadout.InstallationInstance), game.CheckRules)
            .Where(r => r.Id == "spawners")
            .Select(r => FromSoftDiagnostics.CreateItemSpawnerPresent(r.Message));
}

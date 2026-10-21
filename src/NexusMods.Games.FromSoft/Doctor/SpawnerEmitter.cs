using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.Games.FromSoft.EldenRing;

namespace NexusMods.Games.FromSoft.Doctor;

/// <summary>Warns about each item-spawner DLL in the game folder.</summary>
public class SpawnerEmitter : AEldenRingCheckEmitter
{
    protected override IEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout)
        => EldenRingChecks.GameFolder(EldenRingPaths.GameFolder(loadout.InstallationInstance))
            .Where(r => r.Id == "spawners")
            .Select(r => FromSoftDiagnostics.CreateItemSpawnerPresent(r.Message));
}

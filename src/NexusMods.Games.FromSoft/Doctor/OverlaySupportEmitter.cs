using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.Games.FromSoft.Overlay;

namespace NexusMods.Games.FromSoft.Doctor;

/// <summary>
/// Reports a loadout with mods on a machine where the overlay cannot be made. A loadout without mods starts through
/// Steam and needs no sandbox, so it is not reported.
/// </summary>
public class OverlaySupportEmitter(IExecutableLocator executables, ISandboxProbe probe) : AEldenRingCheckEmitter
{
    protected override IEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout)
    {
        if (loadout.InstallationInstance.Game is not IFromSoftGame game || !game.Loader.IsModded(loadout)) yield break;

        var result = EldenRingChecks.OverlaySupport(executables, probe);
        if (result.Level == CheckLevel.Fail) yield return FromSoftDiagnostics.CreateOverlaySupportMissing(result.Message);
    }
}

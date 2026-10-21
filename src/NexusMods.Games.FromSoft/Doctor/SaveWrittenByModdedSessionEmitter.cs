using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Launch;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.Doctor;

/// <summary>
/// Reports each vanilla save file a modded session changed, from the records the save gate keeps. A record stays
/// until the saves page dismisses it.
/// </summary>
public class SaveWrittenByModdedSessionEmitter(ISettingsManager settings, IFileSystem fileSystem) : IFromSoftDiagnosticEmitter
{
    // Reads the ledger's records, not the sync tree, so it gives the same answer without one.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (loadout.InstallationInstance.Game is not IFromSoftGame) yield break;

        var ledger = SaveLedger.For(settings.Get<FromSoftSettings>(), fileSystem, loadout.InstallationInstance);
        foreach (var record in ledger.Written)
        {
            var outcome = record.Restored
                ? "The file was put back as it was before the session."
                : "The file could not be put back, so it still holds what the session wrote; the copy from before the session is kept.";
            yield return FromSoftDiagnostics.CreateSaveWrittenByModdedSession(record.Save, record.Backup, outcome);
        }
    }
}

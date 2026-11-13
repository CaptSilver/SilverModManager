using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Diagnostics;

/// <summary>
/// Warns when the backups of a game's own files pass the backup limit and offers to drop the ones no loadout needs.
/// The backups belong to the game, not to a loadout, so only the game's first loadout carries the warning.
/// </summary>
public class BackupSpaceEmitter(GameBackupSpace space, BackupLimit limit) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (!loadout.IsVisible()) yield break;
        var first = Loadout.FindByInstallation(loadout.Db, loadout.Installation).Where(l => l.IsVisible()).Min(l => l.Id.Value);
        if (first != loadout.Id.Value) yield break;

        if (await space.MeasureIfOver(loadout.InstallationId, limit.Maximum) is not { } report) yield break;

        var reclaimable = report.RemovableCount == 0
            ? "Every one of them is needed by a loadout to put the game folder back, so none can go."
            : $"{BackupOverflow.FormatSize(report.Removable)} of it ({BackupOverflow.Files(report.RemovableCount)}) is not needed by any loadout and can be cleaned up. Backups a loadout still needs are kept.";

        var diagnostic = BackupSpaceDiagnostics.CreateBackupsUseALotOfSpace(BackupOverflow.FormatSize(report.Total), reclaimable);
        yield return report.RemovableCount == 0 ? diagnostic : diagnostic with { Actions = [DiagnosticActions.CleanGameFileBackups()] };
    }
}

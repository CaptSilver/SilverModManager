using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel.Undo;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.Undo;

/// <summary>
/// Ignoring a Health Check finding writes a restore point first, so reverting to it brings the finding back.
/// </summary>
public class DismissalRestorePointTests(ITestOutputHelper helper) : ANexusCollectionTest<DismissalRestorePointTests>(helper)
{
    private UndoService UndoService => ServiceProvider.GetRequiredService<UndoService>();

    [Fact]
    public async Task ARevertRemovesADismissalWrittenAfterTheRestorePoint()
    {
        var loadout = await CreateLoadout();
        await LoadoutSnapshot.WriteRestorePoint(Connection, loadout.LoadoutId);
        var restorePoint = UndoService.RevisionsFor(loadout.Id).OrderBy(r => r.Revision.TxEntity).Last().Revision;

        DiagnosticDismissalId dismissal;
        using (var tx = Connection.BeginTransaction())
        {
            var row = new DiagnosticDismissal.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                Source = "SilverModManager.Games.Generic",
                Number = 11,
                Subject = "Game|a.dll",
                Title = "a title",
            };
            dismissal = (await tx.Commit()).Remap(row).DiagnosticDismissalId;
        }
        await StampRevision(loadout.Id);
        DiagnosticDismissal.Load(Connection.Db, dismissal).IsValid().Should().BeTrue();

        var result = await UndoService.RevertTo(restorePoint);

        result.Residual.Should().BeEmpty();
        DiagnosticDismissal.Load(Connection.Db, dismissal).IsValid().Should().BeFalse();
    }
}

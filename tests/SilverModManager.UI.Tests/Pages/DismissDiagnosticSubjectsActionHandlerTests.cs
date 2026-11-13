using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// Ignore writes the dismissals the finding names, once each, behind a restore point.
/// </summary>
public class DismissDiagnosticSubjectsActionHandlerTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly DiagnosticId Id = new("SilverModManager.Games.Generic", number: 12);

    private (DismissDiagnosticSubjectsActionHandler Handler, IDiagnosticManager Diagnostics) Build()
    {
        var diagnostics = Substitute.For<IDiagnosticManager>();
        return (new DismissDiagnosticSubjectsActionHandler(Connection, diagnostics), diagnostics);
    }

    [Fact]
    public async Task WritesOneRowPerNewSubject()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Dismiss Writes");
        var (handler, diagnostics) = Build();
        var action = DiagnosticActions.DismissDiagnosticSubjects(Id, "Loader Files", [("Game|a.dll", "XxHash3:AA"), ("Game|b.dll", null)]);

        await handler.ExecuteAsync(loadout.LoadoutId, action.Arguments);

        var rows = DiagnosticDismissal.FindByLoadout(Connection.Db, loadout.LoadoutId).ToArray();
        rows.Select(r => (r.Subject, r.Identity.HasValue ? r.Identity.Value : null)).Should().BeEquivalentTo([("Game|a.dll", (string?)"XxHash3:AA"), ("Game|b.dll", null)]);
        rows.Should().OnlyContain(r => r.Title == "Loader Files" && r.Source == Id.Source && r.Number == Id.Number);
        await diagnostics.Received(1).RefreshAsync(loadout.LoadoutId);
    }

    [Fact]
    public async Task ADuplicateSubjectIsSkipped()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Dismiss Duplicate");
        var (handler, _) = Build();
        var action = DiagnosticActions.DismissDiagnosticSubjects(Id, "Loader Files", [("Game|a.dll", "XxHash3:AA")]);

        await handler.ExecuteAsync(loadout.LoadoutId, action.Arguments);
        await handler.ExecuteAsync(loadout.LoadoutId, action.Arguments);

        DiagnosticDismissal.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle();
    }

    [Fact]
    public async Task ANewIdentityAtAnIgnoredPathIsRecordedBesideTheOldOne()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Dismiss Identity");
        var (handler, _) = Build();

        await handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.DismissDiagnosticSubjects(Id, "t", [("Game|a.dll", "XxHash3:AA")]).Arguments);
        await handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.DismissDiagnosticSubjects(Id, "t", [("Game|a.dll", "XxHash3:BB")]).Arguments);

        DiagnosticDismissal.FindByLoadout(Connection.Db, loadout.LoadoutId).Select(r => r.Identity.Value).Should().BeEquivalentTo(["XxHash3:AA", "XxHash3:BB"]);
    }

    [Fact]
    public async Task WritesARestorePointBeforeTheRows()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Dismiss Restore Point");
        var (handler, _) = Build();
        var before = RestorePoints(loadout);

        await handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.DismissDiagnosticSubjects(Id, "t", [("Game|a.dll", null)]).Arguments);

        RestorePoints(loadout).Should().Be(before + 1);
        // Reverting to the restore point brings the finding back only if the row came after it.
        var db = Connection.Db;
        Connection.Query<long>($"""
            SELECT COUNT(*) FROM mdb_Datoms(Db=>{db}, A=>'LoadoutSnapshot/Snapshot') s, mdb_Datoms(Db=>{db}, A=>'DiagnosticDismissal/Loadout') d
            WHERE s.V = {loadout.Id} AND d.V = {loadout.Id} AND s.T < d.T
            """).First().Should().BeGreaterThan(0, "the restore point is written before the rows");
    }

    private long RestorePoints(Loadout.ReadOnly loadout)
    {
        var db = Connection.Db;
        return Connection.Query<long>($"SELECT COUNT(*) FROM mdb_Datoms(Db=>{db}, A=>'LoadoutSnapshot/Snapshot') WHERE V = {loadout.Id}").First();
    }

    [Fact]
    public void RunsUnattended() => Build().Handler.RunsUnattended.Should().BeTrue();
}

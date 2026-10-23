using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Games.Larian.BaldursGate3.Emitters;
using NexusMods.Games.Larian.BaldursGate3.SortOrder;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Larian.Tests.BaldursGate3.Modsettings;

/// <summary>
/// The game throwing the mod list away: the app puts its list back once, and when the game does it again it
/// stops writing the file, until the user takes the list back or changes the order.
/// </summary>
public class ModsettingsResetDetectionTests(ITestOutputHelper helper) : ModsettingsFixture(helper)
{
    private static readonly DiagnosticId ReplacedId = new("NexusMods.Games.Larian.BaldursGate3", number: 12);
    private static readonly DiagnosticId ResetId = new("NexusMods.Games.Larian.BaldursGate3", number: 13);

    private async Task<Loadout.ReadOnly> AppliedTwoPaks()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await InstallMod(loadout, "Beta mod", "beta.pak", BetaPak());
        await WriteModsettings(BaseEntries);
        await Apply(loadout);
        DiskNames().Should().Equal("GustavX", "HonourX", "Alpha", "Beta");
        return loadout;
    }

    /// <summary>The game starts, dislikes the mod list and writes one with only its own modules.</summary>
    private async Task GameThrowsTheListAway(Loadout.ReadOnly loadout)
    {
        await WriteModsettings(BaseEntries);
        await Apply(loadout);
    }

    private async Task<Loadout.ReadOnly> Held()
    {
        var loadout = await AppliedTwoPaks();
        await GameThrowsTheListAway(loadout);
        await GameThrowsTheListAway(loadout);
        return loadout;
    }

    private int ResetCount(Loadout.ReadOnly loadout) =>
        Notice(loadout) is { } notice && notice.ResetCount.HasValue ? notice.ResetCount.Value : 0;

    private string? HoldSummary(Loadout.ReadOnly loadout)
    {
        string? summary = null;
        using var _ = ((IHeldGameEditSource)Variety).HoldSummary(SortOrderOf(loadout)).Subscribe(s => summary = s);
        return summary;
    }

    private async Task<List<Diagnostic>> Diagnose<TEmitter>(Loadout.ReadOnly loadout) where TEmitter : ILoadoutDiagnosticEmitter
    {
        var emitter = Game.DiagnosticEmitters.OfType<TEmitter>().Single();
        return await emitter.Diagnose(Refresh(loadout), CancellationToken.None).ToListAsync();
    }

    private string Summary(Diagnostic diagnostic) => diagnostic.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>());

    [Fact]
    public async Task AnEmptiedFileWithPaksOnDiskIsRestoredTheFirstTime()
    {
        var loadout = await AppliedTwoPaks();

        await GameThrowsTheListAway(loadout);

        ResetCount(loadout).Should().Be(1);
        DiskNames().Should().Equal("GustavX", "HonourX", "Alpha", "Beta");
        HoldSummary(loadout).Should().BeNull();
    }

    [Fact]
    public async Task TheFirstResetNamesTheNewlyActivePaksInTheNote()
    {
        var loadout = await AppliedTwoPaks();

        await GameThrowsTheListAway(loadout);

        Notice(loadout)!.Value.Summary.Should().Contain("put it back").And.Contain("alpha.pak").And.Contain("beta.pak");
    }

    [Fact]
    public async Task TheSecondTimeTheFileIsLeftAsTheGameWroteIt()
    {
        var loadout = await Held();

        ResetCount(loadout).Should().Be(2);
        DiskNames().Should().Equal("GustavX", "HonourX");
        HoldSummary(loadout).Should().NotBeNullOrEmpty().And.Contain("alpha.pak");
    }

    [Fact]
    public async Task AHeldFileStaysAsItIsOnTheNextApplies()
    {
        var loadout = await Held();

        await Apply(loadout);
        await Apply(loadout);

        ResetCount(loadout).Should().Be(2);
        DiskNames().Should().Equal("GustavX", "HonourX");
        HoldSummary(loadout).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AHeldFileKeepsWhatTheGameListed()
    {
        var loadout = await Held();
        await WriteModsettings([.. BaseEntries, Entry("Alpha", "Alpha", "0", AlphaUuid)]);

        await Apply(loadout);

        DiskNames().Should().Equal("GustavX", "HonourX", "Alpha");
        ResetCount(loadout).Should().Be(2);
    }

    [Fact]
    public async Task ReleasingTheHoldWritesTheAppsListOnTheNextApply()
    {
        var loadout = await Held();

        await ((IHeldGameEditSource)Variety).Release(SortOrderOf(loadout));

        ResetCount(loadout).Should().Be(0);
        HoldSummary(loadout).Should().BeNull();
        await Apply(loadout);
        DiskNames().Should().Equal("GustavX", "HonourX", "Alpha", "Beta");
        ResetCount(loadout).Should().Be(0, "putting the list back is not the game throwing it away");
    }

    [Fact]
    public async Task SwitchingAPakClearsTheHold()
    {
        var loadout = await Held();

        await Variety.SetEnabled(SortOrderOf(loadout), new SortItemKey<string>("beta.pak"), false);

        ResetCount(loadout).Should().Be(0);
        HoldSummary(loadout).Should().BeNull();
        await Apply(loadout);
        DiskNames().Should().Equal("GustavX", "HonourX", "Alpha");
    }

    [Fact]
    public async Task MovingAPakClearsTheHold()
    {
        var loadout = await Held();

        await ((Bg3PakSortOrderVariety)Variety).MoveItemDelta(SortOrderOf(loadout), new SortItemKey<string>("alpha.pak"), 1);

        ResetCount(loadout).Should().Be(0);
        HoldSummary(loadout).Should().BeNull();
    }

    [Fact]
    public async Task ReconcilingTheOrderKeepsTheHold()
    {
        var loadout = await Held();

        await Variety.ReconcileSortOrder(SortOrderOf(loadout));

        ResetCount(loadout).Should().Be(2);
    }

    [Fact]
    public async Task AnEmptiedFileWithNoPaksOnDiskIsNotAReset()
    {
        var loadout = await AppliedTwoPaks();
        OnDisk(Pak("alpha.pak")).Delete();
        OnDisk(Pak("beta.pak")).Delete();

        await GameThrowsTheListAway(loadout);

        ResetCount(loadout).Should().Be(0);
    }

    [Fact]
    public async Task AnEmptyFileWithNothingWrittenIsNotAReset()
    {
        var loadout = await CreateLoadout();
        await WriteModsettings(BaseEntries);
        await Apply(loadout);

        await GameThrowsTheListAway(loadout);

        ResetCount(loadout).Should().Be(0);
    }

    [Fact]
    public async Task ASingleRemovedPakIsNotAReset()
    {
        var loadout = await AppliedTwoPaks();

        await WriteModsettings([.. BaseEntries, Entry("Alpha", "Alpha", "0", AlphaUuid)]);
        await Apply(loadout);

        ResetCount(loadout).Should().Be(0);
    }

    [Fact]
    public async Task AFileListingAPakTheAppLeftOutIsNotAReset()
    {
        var loadout = await AppliedTwoPaks();
        await Variety.SetEnabled(SortOrderOf(loadout), new SortItemKey<string>("beta.pak"), false);
        await Apply(loadout);
        DiskNames().Should().Equal("GustavX", "HonourX", "Alpha");

        // A list that names a pak of the loadout is the game editing it, which the replaced notice reports.
        await WriteModsettings([.. BaseEntries, Entry("Beta", "Beta", "0", BetaUuid)]);
        await Apply(loadout);

        ResetCount(loadout).Should().Be(0);
    }

    [Fact]
    public async Task TheReplacedWarningCarriesTheSummaryOfTheNotice()
    {
        var loadout = await AppliedTwoPaks();
        await WriteModsettings([.. BaseEntries, Entry("Alpha", "Alpha", "0", AlphaUuid)]);
        await Apply(loadout);

        var diagnostic = (await Diagnose<Bg3ModListReplacedEmitter>(loadout)).Should().ContainSingle().Subject;

        diagnostic.Id.Should().Be(ReplacedId);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        Summary(diagnostic).Should().Contain("replaced");
        diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("beta.pak");
    }

    [Fact]
    public async Task NoReplacedWarningWithoutANotice()
    {
        var loadout = await AppliedTwoPaks();
        await Apply(loadout);

        (await Diagnose<Bg3ModListReplacedEmitter>(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task OneResetMakesNoResetWarning()
    {
        var loadout = await AppliedTwoPaks();
        await GameThrowsTheListAway(loadout);

        (await Diagnose<Bg3ModListResetEmitter>(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task TwoResetsMakeTheResetWarningNamingThePaks()
    {
        var loadout = await Held();

        var diagnostic = (await Diagnose<Bg3ModListResetEmitter>(loadout)).Should().ContainSingle().Subject;

        diagnostic.Id.Should().Be(ResetId);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        Summary(diagnostic).Should().Contain("twice");
        diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>())
            .Should().Contain("alpha.pak").And.Contain("Restore the mod list");
        (await Diagnose<Bg3ModListReplacedEmitter>(loadout)).Should().BeEmpty("the hold is reported by the reset warning");
    }

    [Fact]
    public async Task AnApplyThatChangesNothingLeavesTheNoticeAlone()
    {
        var loadout = await AppliedTwoPaks();
        await Apply(loadout);
        var noticeId = Notice(loadout)!.Value.Id;
        var before = Connection.Db.Get(noticeId).Max(d => d.T);

        await Apply(loadout);

        Connection.Db.Get(noticeId).Max(d => d.T).Should().Be(before, "the plan and the note are what they were");
        Lines(Notice(loadout)!.Value.PreviousWrittenPaks).Should().BeEmpty("only one list was ever written");
    }
}

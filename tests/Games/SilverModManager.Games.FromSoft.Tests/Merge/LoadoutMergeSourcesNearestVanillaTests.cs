using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Sdk.Loadouts;
using static SilverModManager.Games.FromSoft.Tests.Merge.StaleTalkFixture;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// Which library dump serves a packed file. A dump whose regulation the app kept as a validated baseline counts, and
/// when several copies are on offer the one nearest the mods' own files is used.
/// </summary>
public class LoadoutMergeSourcesNearestVanillaTests(ITestOutputHelper helper) : AVanillaDumpTest<LoadoutMergeSourcesNearestVanillaTests>(helper)
{
    private LoadoutMergeSources Sources => ServiceProvider.GetRequiredService<LoadoutMergeSources>();

    private MergePlanCache Plans => ServiceProvider.GetRequiredService<MergePlanCache>();

    private MergeDiagnosticsEmitter Emitter => ServiceProvider.GetServices<IFromSoftDiagnosticEmitter>().OfType<MergeDiagnosticsEmitter>().Single();

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private Task AddDump(string name, byte[] regulation, byte[] talk)
        => AddArchive(name, ("Vanilla/regulation.bin", regulation), ("Vanilla/m00.talkesdbnd.dcx", talk));

    // Remover is installed first, so Adder has the higher priority and is the side that wins a conflict.
    private async Task<Loadout.ReadOnly> TalkMods(byte[] first, byte[] second)
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Remover", (TalkPath, first));
        await InstallPackage(loadout.LoadoutId, "Adder", (TalkPath, second));
        return Refresh(loadout);
    }

    [Fact]
    public async Task ADumpWhoseRegulationIsAValidatedBaselineOfThatArchiveServesAPackedFile()
    {
        var regulation = Regulation(OlderVersion, 0xB0);
        var loadout = await TalkMods(Bytes("one"), Bytes("two"));
        await AddDump("Dump", regulation, Bytes("vanilla talk"));
        loadout = Refresh(loadout);
        Sources.Index(loadout).Lookup(TalkPath)!.Outcome.Reason.Should().Be(MergeOutcome.NoVanillaSource, "nothing vouches for the regulation yet");

        KeepValidatedBaseline(OlderVersion, regulation, "Dump.zip: Vanilla/regulation.bin");

        var candidate = Sources.Index(loadout).Lookup(TalkPath)!;
        candidate.Outcome.Merges.Should().BeTrue();
        var notes = new List<IMergeNote>();
        Sources.SingleVanilla(loadout, TalkPath, candidate.Contributors, MergeSourceMode.Report, notes).Should().Equal(Bytes("vanilla talk"));
        var served = notes.Should().ContainSingle().Which.Should().BeOfType<VanillaServedFromDump>().Subject;
        served.Should().Be(new VanillaServedFromDump(TalkPath, "Dump.zip", OlderVersion, Validated: true));
        served.Describe().Should().Contain("Dump.zip").And.Contain("validated");
    }

    [Fact]
    public async Task ABaselineValidatedUnderTheArchivesStoredFileNameStillBelongsToThatArchive()
    {
        // A Nexus download is stored under a temporary name and its library item carries the readable one. The baseline
        // was recorded with whichever name the app used at the time, so both must find the archive.
        var regulation = Regulation(OlderVersion, 0xB0);
        var loadout = await TalkMods(Bytes("one"), Bytes("two"));
        var dump = await AddArchive("eb256129", ("Vanilla/regulation.bin", regulation), ("Vanilla/m00.talkesdbnd.dcx", Bytes("vanilla talk")));
        await RenameLibraryItem(dump, "Item and Enemy Randomizer");
        KeepValidatedBaseline(OlderVersion, regulation, "eb256129.zip: Vanilla/regulation.bin");

        var candidate = Sources.Index(Refresh(loadout)).Lookup(TalkPath)!;

        candidate.Outcome.Merges.Should().BeTrue();
        ServiceProvider.GetRequiredService<VanillaDumps>().Unconfirmed(Connection.Db, GameInstallation).Should().BeEmpty();
    }

    [Fact]
    public async Task TheOlderDumpTheModsWereMadeFromBeatsTheNewerCopyWhenItIsKnownByItsStoredFileName()
    {
        var older = Regulation(OlderVersion, 0xB0);
        var loadout = await TalkMods(Remover, Adder);
        var dump = await AddArchive("eb256129", ("Vanilla/regulation.bin", older), ("Vanilla/m00.talkesdbnd.dcx", Old));
        await RenameLibraryItem(dump, "Item and Enemy Randomizer");
        KeepValidatedBaseline(OlderVersion, older, "eb256129.zip: Vanilla/regulation.bin");
        await AddDump("Current", EldenRingInstall.RegulationFile, Patched);
        loadout = Refresh(loadout);

        var report = await ServiceProvider.GetRequiredService<IMergeReports>().ForLoadout(loadout.LoadoutId, CancellationToken.None);

        var entry = report.Entries.Should().ContainSingle(e => e.Path == TalkPath).Which;
        entry.Outcome.Should().Be(MergeReportOutcome.Merged, entry.Reason);
        entry.VanillaSource.Should().Contain("Item and Enemy Randomizer").And.NotContain("Current.zip");
    }

    [Fact]
    public async Task AValidatedBaselineFromAnotherArchiveDoesNotMakeThisArchiveADump()
    {
        var regulation = Regulation(OlderVersion, 0xB0);
        var loadout = await TalkMods(Bytes("one"), Bytes("two"));
        await AddDump("Dump", regulation, Bytes("vanilla talk"));
        KeepValidatedBaseline(OlderVersion, regulation, "Elsewhere.zip: regulation.bin");

        var candidate = Sources.Index(Refresh(loadout)).Lookup(TalkPath)!;

        candidate.Outcome.Reason.Should().Be(MergeOutcome.NoVanillaSource);
    }

    [Fact]
    public async Task AValidatedDumpIsNotListedAsUnconfirmed()
    {
        var regulation = Regulation(OlderVersion, 0xB0);
        var loadout = await TalkMods(Bytes("one"), Bytes("two"));
        await AddDump("Dump", regulation, Bytes("vanilla talk"));
        var dumps = ServiceProvider.GetRequiredService<VanillaDumps>();
        dumps.Unconfirmed(Connection.Db, GameInstallation).Should().ContainSingle("the app cannot confirm the regulation yet");

        KeepValidatedBaseline(OlderVersion, regulation, "Dump.zip: Vanilla/regulation.bin");

        dumps.Unconfirmed(Connection.Db, GameInstallation).Should().BeEmpty();
    }

    [Fact]
    public async Task ADumpAndAValidatedDumpBothOfferingTheFileAreChosenByTheirDistanceToTheMods()
    {
        KeepValidatedBaseline(OlderVersion, Regulation(OlderVersion, 0xB0), "Old.zip: Vanilla/regulation.bin");
        var loadout = await TalkMods(Remover, Adder);
        await AddDump("Old", Regulation(OlderVersion, 0xB0), Old);
        await AddDump("Current", EldenRingInstall.RegulationFile, Patched);
        loadout = Refresh(loadout);
        var contributors = Sources.Index(loadout).Lookup(TalkPath)!.Contributors;

        var notes = new List<IMergeNote>();
        Sources.SingleVanilla(loadout, TalkPath, contributors, MergeSourceMode.Report, notes).Should().Equal(Old, "the mods were made from the older copy");

        notes.OfType<VanillaServedFromDump>().Should().ContainSingle().Which.Should().Be(new VanillaServedFromDump(TalkPath, "Old.zip", OlderVersion, Validated: true));
        var nearest = notes.OfType<VanillaNearestChosen>().Should().ContainSingle().Which;
        nearest.Of.Should().Be(2);
        nearest.Distance.Should().BePositive();
        nearest.Describe().Should().Contain("Old.zip").And.Contain(nearest.Distance.ToString());
        nearest.Describe().Should().NotContain(TalkPath, "the report entry and the Health Check diagnostic already name the path");
        var plan = Plans.Current(loadout, MergeSourceMode.Report);
        plan.Unmerged.Should().BeEmpty();
        plan.Merged.Should().ContainSingle(m => m.Path == TalkPath);
    }

    [Fact]
    public async Task TheChoiceOfVanillaCopyIsInTheMergeReportAndNotInTheHealthCheck()
    {
        KeepValidatedBaseline(OlderVersion, Regulation(OlderVersion, 0xB0), "Old.zip: Vanilla/regulation.bin");
        var loadout = await TalkMods(Remover, Adder);
        await AddDump("Old", Regulation(OlderVersion, 0xB0), Old);
        await AddDump("Current", EldenRingInstall.RegulationFile, Patched);
        loadout = Refresh(loadout);

        var diagnostics = new List<Diagnostic>();
        await foreach (var diagnostic in Emitter.Diagnose(loadout, CancellationToken.None)) diagnostics.Add(diagnostic);
        var report = await ServiceProvider.GetRequiredService<IMergeReports>().ForLoadout(loadout.LoadoutId, CancellationToken.None);

        var writer = new PlainDiagnosticWriter();
        diagnostics.Should().NotContain(d => d.FormatSummary(writer, DiagnosticWriterMode.PlainText).Contains("vanilla copies were on offer"));
        diagnostics.Should().NotContain(d => d.FormatDetails(writer, DiagnosticWriterMode.PlainText).Contains("vanilla copies were on offer"));
        report.Entries.Should().ContainSingle(e => e.Path == TalkPath).Which.Notes.Should().Contain(n => n.Contains("vanilla copies were on offer"));
    }

    [Fact]
    public async Task TheNewerCopyAloneStillFailsTheMergeNoBaseCanKeepWhole()
    {
        var loadout = await TalkMods(Remover, Adder);
        await AddDump("Current", EldenRingInstall.RegulationFile, Patched);

        var plan = Plans.Current(Refresh(loadout), MergeSourceMode.Report);

        plan.Merged.Should().NotContain(m => m.Path == TalkPath);
        var fallback = plan.Unmerged.Should().ContainSingle(u => u.Path == TalkPath).Which;
        fallback.Outcome.Reason.Should().Contain("calls machine 20");
        fallback.VanillaSource.Should().Contain("Current.zip");
    }

    [Fact]
    public async Task TheReportSaysWhichModTheMergeUsedAsTheBaseAndWhyThePriorityOrderFailed()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Deleter", (TalkPath, DeletedMachineTalkFixture.Deleter));
        await InstallPackage(loadout.LoadoutId, "Hooker", (TalkPath, DeletedMachineTalkFixture.Hooker));
        await AddDump("Current", EldenRingInstall.RegulationFile, DeletedMachineTalkFixture.Vanilla);

        var report = await ServiceProvider.GetRequiredService<IMergeReports>().ForLoadout(Refresh(loadout).LoadoutId, CancellationToken.None);

        var entry = report.Entries.Should().ContainSingle(e => e.Path == TalkPath).Which;
        entry.Outcome.Should().Be(MergeReportOutcome.Merged, entry.Reason);
        entry.Notes.Should().ContainSingle(n => n.Contains("Deleter.zip as the base") && n.Contains("calls machine 20"));
        entry.VanillaSource.Should().Contain("Current.zip");
    }

    [Fact]
    public async Task AMergeThatFailsInEveryOrderNamesTheVanillaCopyItWasTriedAgainstInTheReport()
    {
        KeepValidatedBaseline(OlderVersion, Regulation(OlderVersion, 0xB0), "Old.zip: Vanilla/regulation.bin");
        var loadout = await TalkMods(Bytes("one"), Bytes("two"));
        await AddDump("Old", Regulation(OlderVersion, 0xB0), Old);
        loadout = Refresh(loadout);

        var report = await ServiceProvider.GetRequiredService<IMergeReports>().ForLoadout(loadout.LoadoutId, CancellationToken.None);

        var entry = report.Entries.Should().ContainSingle(e => e.Path == TalkPath).Which;
        entry.Outcome.Should().Be(MergeReportOutcome.NotMergeable);
        entry.Reason.Should().StartWith("the merge failed: ");
        entry.VanillaSource.Should().Contain("Old.zip").And.Contain(TalkPath);
        entry.Notes.Should().ContainSingle().Which.Should().EndWith("wins by loadout priority; the other copies are ignored");
    }

    [Fact]
    public async Task AFileOnlyOneCopyOffersNeedsNoMeasuring()
    {
        KeepValidatedBaseline(OlderVersion, Regulation(OlderVersion, 0xB0), "Old.zip: Vanilla/regulation.bin");
        var loadout = await TalkMods(Bytes("one"), Bytes("two"));
        await AddDump("Old", Regulation(OlderVersion, 0xB0), Bytes("old talk"));
        loadout = Refresh(loadout);
        var contributors = Sources.Index(loadout).Lookup(TalkPath)!.Contributors;

        var notes = new List<IMergeNote>();
        Sources.SingleVanilla(loadout, TalkPath, contributors, MergeSourceMode.Report, notes);

        notes.OfType<VanillaNearestChosen>().Should().BeEmpty();
    }
}

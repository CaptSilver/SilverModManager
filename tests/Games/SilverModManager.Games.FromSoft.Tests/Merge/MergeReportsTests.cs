using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class MergeReportsTests(ITestOutputHelper helper) : AVanillaDumpTest<MergeReportsTests>(helper)
{
    private const string Msg = "msg/engus/item.msgbnd.dcx";
    private const string Talk = "script/talk/m00.talkesdbnd.dcx";
    private const string RegulationPath = "regulation.bin";

    private bool _failNextIndex;

    private IMergeReports Reports => ServiceProvider.GetRequiredService<IMergeReports>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        // Fails the first index it is asked for after the test arms it, the way a transient read error would.
        services.AddSingleton<LoadoutMergeSources>(provider => new FailingOnceSources(
            this,
            provider.GetRequiredService<IFileStore>(),
            provider.GetRequiredService<RegulationBaselines>(),
            provider.GetRequiredService<VanillaDumps>(),
            provider.GetRequiredService<ArchiveVanillas>(),
            provider.GetRequiredService<InstalledBuilds>(),
            provider.GetRequiredService<ILogger<LoadoutMergeSources>>()));
        return services;
    }

    private sealed class FailingOnceSources(MergeReportsTests test, IFileStore fileStore, RegulationBaselines baselines, VanillaDumps dumps, ArchiveVanillas archives, InstalledBuilds builds, ILogger<LoadoutMergeSources> logger)
        : LoadoutMergeSources(fileStore, baselines, dumps, archives, builds, logger)
    {
        public override MergePathIndex Index(Loadout.ReadOnly loadout)
        {
            if (!test._failNextIndex) return base.Index(loadout);

            test._failNextIndex = false;
            throw new IOException("the library could not be read");
        }
    }

    private AbsolutePath Mods => GameInstallation.Locations[LocationId.Me3Mods].Path;

    private static byte[] Msgbnd(string text)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>>
        {
            [1] = new Dictionary<int, string?> { [10] = text },
        }, Formats);

    // The game's own regulation with one member changed, so each mod edits something the other leaves alone.
    private static byte[] RegulationEditing(int id, string content)
        => RegulationFixture.BuildFromEntries(
            RegulationFixture.GoldenEntries.Select(e => e.Id == id ? (e.Id, e.Name, System.Text.Encoding.ASCII.GetBytes(content)) : e).ToArray(),
            GameVersion, Formats);

    private static byte[] RegulationEditingAt(string version, int id, string content)
        => RegulationFixture.BuildFromEntries(
            RegulationFixture.GoldenEntries.Select(e => e.Id == id ? (e.Id, e.Name, System.Text.Encoding.ASCII.GetBytes(content)) : e).ToArray(),
            version, Formats);

    private async Task<LoadoutItemGroup.ReadOnly> InstallMod(LoadoutId loadout, Optional<LoadoutItemGroupId> parent, string name, params (string Path, byte[] Content)[] files)
    {
        var archive = await AddArchive(name, files);
        var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, parent, installer);
        return result.LoadoutItemGroup!.Value;
    }

    private string[] DataRootAndModsListing()
        => [.. (DataRoot.DirectoryExists() ? TreeSnapshot.Take(DataRoot) : []), .. (Mods.DirectoryExists() ? TreeSnapshot.Take(Mods) : [])];

    [Fact]
    public async Task AMergingMsgbndPairIsMergedWithBothModsInPriorityOrderAndTheTextKeptNote()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (Msg, Msgbnd("alpha text")));
        await InstallPackage(loadout.LoadoutId, "Beta", (Msg, Msgbnd("beta text")));
        loadout = Refresh(loadout);
        var plan = ServiceProvider.GetRequiredService<MergePlanCache>().Current(loadout, MergeSourceMode.Report);

        var report = await Reports.ForLoadout(loadout.LoadoutId, CancellationToken.None);

        report.Refusal.Should().BeNull();
        var entry = report.Entries.Should().ContainSingle().Which;
        entry.Path.Should().Be(Msg);
        entry.Strategy.Should().Be("fmg-union");
        entry.Outcome.Should().Be(MergeReportOutcome.Merged, entry.Reason);
        entry.Contributors.Should().Equal(plan.Merged.Single().Contributors.Select(c => c.ModName));
        entry.Contributors.Should().BeEquivalentTo("Alpha.zip", "Beta.zip");
        entry.Notes.Should().Equal(plan.Merged.Single().Notes.Select(n => n.Describe()));
        entry.Notes.Should().Contain(n => n.Contains("both mods ship it and differ"));
        entry.VanillaSource.Should().BeEmpty("a union compares nothing");
        entry.Reason.Should().BeEmpty();
        report.PriorityOrderHint.Should().Contain("Move up / Move down").And.Contain("File Conflicts");
    }

    [Fact]
    public async Task ATalkPairWithNoDumpIsNotMergeableForWantOfAVanillaSource()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (Talk, [1, 2, 3]));
        await InstallPackage(loadout.LoadoutId, "Beta", (Talk, [4, 5, 6]));

        var report = await Reports.ForLoadout(loadout.LoadoutId, CancellationToken.None);

        report.Refusal.Should().BeNull("an unmergeable overlap is reported, not refused");
        var entry = report.Entries.Should().ContainSingle().Which;
        entry.Path.Should().Be(Talk);
        entry.Strategy.Should().Be("esd-3way");
        entry.Outcome.Should().Be(MergeReportOutcome.NotMergeable);
        entry.Reason.Should().Be(MergeOutcome.NoVanillaSource);
        entry.Contributors.Should().BeEquivalentTo("Alpha.zip", "Beta.zip");
    }

    [Fact]
    public async Task APairWithNoStrategyIsNotMergeableAndSaysWhichModLoadoutPriorityKeeps()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", ("parts/wp_a.partsbnd.dcx", [1, 2, 3]));
        await InstallPackage(loadout.LoadoutId, "Beta", ("parts/wp_a.partsbnd.dcx", [4, 5, 6]));

        var report = await Reports.ForLoadout(loadout.LoadoutId, CancellationToken.None);

        report.Refusal.Should().BeNull();
        var entry = report.Entries.Should().ContainSingle().Which;
        entry.Outcome.Should().Be(MergeReportOutcome.NotMergeable);
        entry.Reason.Should().Be(MergeOutcome.NoStrategy);
        entry.Notes.Should().ContainSingle().Which.Should().EndWith("wins by loadout priority; the other copies are ignored")
            .And.Match("*.zip wins*");
    }

    [Fact]
    public async Task ARefusedRegulationPairReportsTheTextTheApplyThrowsAndHoldsBackTheOtherMerges()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (RegulationPath, Regulation(GameVersion, 0x10)), (Msg, Msgbnd("alpha text")));
        await InstallPackage(loadout.LoadoutId, "Beta", (RegulationPath, Regulation(OlderVersion, 0x20)), (Msg, Msgbnd("beta text")));
        loadout = Refresh(loadout);

        var report = await Reports.ForLoadout(loadout.LoadoutId, CancellationToken.None);
        var act = async () => await Synchronizer.Synchronize(loadout);
        var thrown = (await act.Should().ThrowAsync<MergeRefusedException>()).Which;

        report.Refusal.Should().Be(thrown.Message);
        report.Entries.Select(e => e.Path).Should().Equal(Msg, RegulationPath);
        var regulation = report.Entries.Single(e => e.Path == RegulationPath);
        regulation.Outcome.Should().Be(MergeReportOutcome.Refused);
        regulation.Strategy.Should().Be("param-rows");
        regulation.Reason.Should().Be(thrown.Message);
        var held = report.Entries.Single(e => e.Path == Msg);
        held.Outcome.Should().Be(MergeReportOutcome.Refused);
        held.Reason.Should().Be("not merged: the apply is refused");
    }

    [Fact]
    public async Task AnEmptyLoadoutReportsNoEntries()
    {
        var loadout = await CreateLoadout();

        var report = await Reports.ForLoadout(loadout.LoadoutId, CancellationToken.None);

        report.Entries.Should().BeEmpty();
        report.Refusal.Should().BeNull();
    }

    [Fact]
    public async Task AModInstalledInsideACollectionIsNamedByTheModNotTheCollection()
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout.LoadoutId, "Collection");
        await InstallPackage(loadout.LoadoutId, "Other", (Msg, Msgbnd("other text")));
        await InstallMod(loadout.LoadoutId, collection.AsLoadoutItemGroup().LoadoutItemGroupId, "Weapons", (Msg, Msgbnd("weapons text")));

        var report = await Reports.ForLoadout(loadout.LoadoutId, CancellationToken.None);

        var names = report.Entries.Should().ContainSingle().Which.Contributors;
        names.Should().HaveCount(2).And.NotContain("Collection");
        names.Should().Contain(n => n.Contains("Weapons"));
    }

    [Fact]
    public async Task ABaselineTheNextApplyWouldKeepIsSaidAndNothingIsWritten()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (RegulationPath, RegulationEditing(1, "BBBB")));
        await InstallPackage(loadout.LoadoutId, "Beta", (RegulationPath, RegulationEditing(3, "CCCC")));
        var before = DataRootAndModsListing();

        var report = await Reports.ForLoadout(loadout.LoadoutId, CancellationToken.None);

        var entry = report.Entries.Should().ContainSingle().Which;
        entry.Outcome.Should().Be(MergeReportOutcome.Merged, entry.Reason);
        entry.VanillaSource.Should().Be(new BaselineWillBeKept(GameVersion).Describe());
        DataRootAndModsListing().Should().Equal(before);
        (DataRoot / "baselines").DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task ModsOfTwoOlderBuildsEachNameTheirOwnVanillaInTheSourceColumn()
    {
        const string other = "11602000";
        KeepBaseline(OlderVersion, RegulationFixture.BuildFromEntries(RegulationFixture.GoldenEntries, OlderVersion, Formats));
        KeepBaseline(other, RegulationFixture.BuildFromEntries(RegulationFixture.GoldenEntries, other, Formats));
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (RegulationPath, RegulationEditingAt(OlderVersion, 1, "BBBB")));
        await InstallPackage(loadout.LoadoutId, "Beta", (RegulationPath, RegulationEditingAt(other, 3, "CCCC")));

        var report = await Reports.ForLoadout(loadout.LoadoutId, CancellationToken.None);

        var entry = report.Entries.Should().ContainSingle().Which;
        entry.Outcome.Should().Be(MergeReportOutcome.Merged, entry.Reason);
        entry.VanillaSource.Should().Contain("Alpha").And.Contain(OlderVersion).And.Contain("Beta").And.Contain(other);
        entry.Notes.Should().NotContain(n => n.Contains("compared against"), "where an ancestor came from has its own column");
    }

    [Fact]
    public async Task ATalkPairBesideADumpNothingVouchesForNamesTheDumpInItsNotes()
    {
        var loadout = await CreateLoadout();
        await AddArchive("Dump", ("Vanilla/regulation.bin", Regulation(OlderVersion, 0xB0)), ("Vanilla/m00.talkesdbnd.dcx", [7, 8, 9]));
        await InstallPackage(loadout.LoadoutId, "Alpha", (Talk, [1, 2, 3]));
        await InstallPackage(loadout.LoadoutId, "Beta", (Talk, [4, 5, 6]));

        var report = await Reports.ForLoadout(loadout.LoadoutId, CancellationToken.None);

        var entry = report.Entries.Should().ContainSingle().Which;
        entry.Outcome.Should().Be(MergeReportOutcome.NotMergeable);
        entry.Reason.Should().Be(MergeOutcome.NoVanillaSource);
        entry.Notes.Should().Equal($"archive Dump.zip holds a regulation.bin of version {OlderVersion} the app cannot confirm as vanilla, so its copy of {Talk} was not used");
    }

    [Fact]
    public async Task ADumpNothingVouchesForIsOnlyNamedUnderThePathsItHoldsACopyOf()
    {
        var loadout = await CreateLoadout();
        await AddArchive("Dump", ("Vanilla/regulation.bin", Regulation(OlderVersion, 0xB0)), ("Vanilla/m10.talkesdbnd.dcx", [7, 8, 9]));
        await InstallPackage(loadout.LoadoutId, "Alpha", (Talk, [1, 2, 3]));
        await InstallPackage(loadout.LoadoutId, "Beta", (Talk, [4, 5, 6]));

        var report = await Reports.ForLoadout(loadout.LoadoutId, CancellationToken.None);

        var entry = report.Entries.Should().ContainSingle().Which;
        entry.Reason.Should().Be(MergeOutcome.NoVanillaSource);
        entry.Notes.Should().BeEmpty("the dump has no copy of this talk file");
    }

    [Fact]
    public async Task AnIndexThatCannotBeBuiltReportsTheApplysRefusalAndNoEntries()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (Msg, Msgbnd("alpha text")));
        await InstallPackage(loadout.LoadoutId, "Beta", (Msg, Msgbnd("beta text")));
        _failNextIndex = true;

        var report = await Reports.ForLoadout(loadout.LoadoutId, CancellationToken.None);

        report.Entries.Should().BeEmpty();
        report.Refusal.Should().Be(MergeRefusedException.PlanFailed(new IOException("the library could not be read")).Message);
    }

    [Theory]
    [InlineData(MergeReportOutcome.Merged, "merged")]
    [InlineData(MergeReportOutcome.Refused, "refused")]
    [InlineData(MergeReportOutcome.NotMergeable, "not merged")]
    public void EveryOutcomeHasOneDisplayLabel(MergeReportOutcome outcome, string label)
    {
        outcome.Label().Should().Be(label);
    }

    [Fact]
    public void TheNoVanillaReasonPointsAtTheFileShownBesideIt()
    {
        MergeOutcome.NoVanillaSource.Should().Contain("this file").And.NotContain("the vanilla file");
    }
}

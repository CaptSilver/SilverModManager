using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class MergePlanCacheTests(ITestOutputHelper helper) : AVanillaDumpTest<MergePlanCacheTests>(helper)
{
    private const string Msg = "msg/engus/item.msgbnd.dcx";

    private MergePlanCache Plans => ServiceProvider.GetRequiredService<MergePlanCache>();

    private static byte[] Msgbnd(string text, int ownId)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>>
        {
            [1] = new Dictionary<int, string?> { [10] = text, [ownId] = "own" },
        }, Formats);

    private async Task<LoadoutItemGroup.ReadOnly> InstallMod(LoadoutId loadout, string name, params (string Path, byte[] Content)[] files)
    {
        var path = TemporaryFileManager.CreateFolder().Path / (name + ".zip");
        await TestArchives.WriteEntries(path, files);
        var archive = await RegisterLocalArchive(path);
        var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: installer);
        return result.LoadoutItemGroup!.Value;
    }

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroup.ReadOnly Alpha, LoadoutItemGroup.ReadOnly Beta)> TwoMsgbndMods()
    {
        var loadout = await CreateLoadout();
        var alpha = await InstallMod(loadout.LoadoutId, "Alpha", (Msg, Msgbnd("alpha text", 11)));
        var beta = await InstallMod(loadout.LoadoutId, "Beta", (Msg, Msgbnd("beta text", 12)));
        return (Refresh(loadout), alpha, beta);
    }

    private const string OtherOlderVersion = "11602000";

    private async Task<Loadout.ReadOnly> TwoRegulationsOfDifferentBuilds()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout.LoadoutId, "Alpha", ("regulation.bin", Regulation(OlderVersion, 0x10)));
        await InstallMod(loadout.LoadoutId, "Beta", ("regulation.bin", Regulation(OtherOlderVersion, 0x20)));
        return Refresh(loadout);
    }

    [Fact]
    public async Task AMissingVanillaOfEachBuildReachesThePlansRefusalAsData()
    {
        var loadout = await TwoRegulationsOfDifferentBuilds();

        var refusal = Plans.Current(loadout, MergeSourceMode.Report).Refusal.Should().NotBeNull().And.Subject.As<MergeRefusal>();

        refusal.Missing.Select(m => m.Version).Should().BeEquivalentTo([OlderVersion, OtherOlderVersion]);
        refusal.Missing.Single(m => m.Version == OlderVersion).Mods.Should().ContainSingle().Which.Should().Contain("Alpha");
        refusal.Describe().Should().Contain(OlderVersion).And.Contain(OtherOlderVersion);
    }

    [Fact]
    public async Task OtherRefusalsCarryNoMissingVanilla()
    {
        var (loadout, _, _) = await TwoMsgbndMods();

        Plans.Current(loadout, MergeSourceMode.Report).Refusal.Should().BeNull();
        new MergeRefusal("x", MergeStrategy.ParamRows, "why").Missing.Should().BeEmpty();
    }

    [Fact]
    public async Task KeepingAVanillaOfEitherBuildInvalidatesTheCachedRefusal()
    {
        var loadout = await TwoRegulationsOfDifferentBuilds();
        var first = Plans.Current(loadout, MergeSourceMode.Report);
        Plans.Current(loadout, MergeSourceMode.Report).Should().BeSameAs(first, "nothing changed");

        KeepBaseline(OlderVersion, Regulation(OlderVersion, 0xB0));
        var second = Plans.Current(loadout, MergeSourceMode.Report);

        second.Should().NotBeSameAs(first);
        second.Refusal!.Missing.Should().ContainSingle().Which.Version.Should().Be(OtherOlderVersion, "the build that is still missing one is all that is left");

        KeepBaseline(OtherOlderVersion, Regulation(OtherOlderVersion, 0xB1));
        var third = Plans.Current(loadout, MergeSourceMode.Report);
        third.Should().NotBeSameAs(second);
        third.Refusal?.Missing.Should().BeNullOrEmpty();

        KeepBaseline(OtherOlderVersion, Regulation(OtherOlderVersion, 0xB2));
        Plans.Current(loadout, MergeSourceMode.Report).Should().NotBeSameAs(third, "the second group's vanilla changed and the first's did not");
    }

    [Fact]
    public async Task AnUnchangedLoadoutGetsTheSamePlanBack()
    {
        var (loadout, _, _) = await TwoMsgbndMods();

        var first = Plans.Current(loadout, MergeSourceMode.Report);
        var second = Plans.Current(Refresh(loadout), MergeSourceMode.Report);

        second.Should().BeSameAs(first);
        first.Merged.Should().ContainSingle().Which.Path.Should().Be(Msg);
    }

    [Fact]
    public async Task APlanThatNeededNothingKeptIsReusedByAnApply()
    {
        var (loadout, _, _) = await TwoMsgbndMods();

        var report = Plans.Current(loadout, MergeSourceMode.Report);

        Plans.Current(loadout, MergeSourceMode.Apply).Should().BeSameAs(report);
    }

    [Fact]
    public async Task AChangeToThePriorityMergesAgain()
    {
        var (loadout, alpha, beta) = await TwoMsgbndMods();
        var before = Plans.Current(loadout, MergeSourceMode.Report);

        var alphaRow = LoadoutItemGroupPriority.FindByTarget(Connection.Db, alpha.LoadoutItemGroupId).Single();
        var betaRow = LoadoutItemGroupPriority.FindByTarget(Connection.Db, beta.LoadoutItemGroupId).Single();
        await LoadoutManager.ResolveFileConflicts([alphaRow.LoadoutItemGroupPriorityId], betaRow.LoadoutItemGroupPriorityId);
        var after = Plans.Current(Refresh(loadout), MergeSourceMode.Report);

        after.Should().NotBeSameAs(before);
        after.Merged.Single().Bytes.Should().NotEqual(before.Merged.Single().Bytes);
        after.Merged.Single().Contributors.Select(c => c.PackageId).Should().Equal("Alpha", "Beta");
    }

    [Fact]
    public async Task AReportThatWouldKeepABaselineIsNotWhatAnApplyGetsAndOnlyTheApplyKeepsIt()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout.LoadoutId, "Alpha", ("regulation.bin", Regulation(GameVersion, 0x10)));
        await InstallMod(loadout.LoadoutId, "Beta", ("regulation.bin", Regulation(GameVersion, 0x20)));
        loadout = Refresh(loadout);
        var baselines = DataRoot / "baselines";

        var report = Plans.Current(loadout, MergeSourceMode.Report);

        baselines.DirectoryExists().Should().BeFalse("a report writes nothing");
        var apply = Plans.Current(loadout, MergeSourceMode.Apply);
        apply.Should().NotBeSameAs(report, "the apply keeps the regulation the report only said it would");
        Directory.GetFiles(baselines.GetFullPath()).Should().NotBeEmpty();
        Plans.Current(loadout, MergeSourceMode.Report).Should().BeSameAs(apply, "what an apply made serves a report too");
    }

    [Fact]
    public async Task AMergedFileWhosePathThePlanLacksThrowsNamingThePath()
    {
        var (loadout, _, _) = await TwoMsgbndMods();
        var file = new MergedFile(Plans, "msg/engus/menu.msgbnd.dcx");

        var act = async () => await file.Write(new MemoryStream(), loadout, []);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("_merged/msg/engus/menu.msgbnd.dcx").And.Contain("disagree");
    }
}

/// <summary>A plan whose vanilla comes from the game's own archives is only as current as that archive's copy.</summary>
public class MergePlanCacheGameArchiveTests(ITestOutputHelper helper) : AModdedTestSoulsTest<MergePlanCacheGameArchiveTests>(helper)
{
    private const string Part = "parts/wp_a_0556.partsbnd.dcx";

    private FromSoftFormatTraits Formats => ((IFromSoftGame)GameInstallation.Game).Formats;

    private void WriteGameArchive(IReadOnlyList<(int Id, string Name, byte[] Data)> vanilla)
        => GameArchiveFixture.Write(RealGameFolder.GetFullPath(), "TestData0", Bhd5Fixture.TestKey, Formats, encrypted: true, lead: 48,
            (Part, PartsbndMods.Bundle(Formats, vanilla)));

    private async Task<Loadout.ReadOnly> GlowAndMoveset()
    {
        var loadout = await CreateLoadout();
        foreach (var (name, members) in new[] { (PartsbndMods.Glow, PartsbndMods.GlowMembers()), (PartsbndMods.Moveset, PartsbndMods.MovesetMembers()) })
        {
            var archive = TemporaryFileManager.CreateFolder().Path / (name + ".zip");
            await TestArchives.WriteEntries(archive, [(Part, PartsbndMods.Bundle(Formats, members))]);
            var registered = await RegisterLocalArchive(archive);
            var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
            await LoadoutManager.InstallItem(registered.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId, installer: installer);
        }
        return Refresh(loadout);
    }

    [Fact]
    public async Task ANewVanillaCopyInTheGameArchivesIsReadIntoTheNextPlan()
    {
        WriteGameArchive(PartsbndMods.Vanilla());
        var loadout = await GlowAndMoveset();
        var plans = ServiceProvider.GetRequiredService<MergePlanCache>();
        var first = plans.Current(loadout, MergeSourceMode.Report);
        first.Merged.Should().ContainSingle().Which.Path.Should().Be(Part);
        plans.Current(Refresh(loadout), MergeSourceMode.Report).Should().BeSameAs(first, "nothing changed");

        // The game's update dropped a model from the part, so the mods' bundles no longer line up with vanilla. Only a
        // plan that read the new copy can know the merge now fails.
        WriteGameArchive([.. PartsbndMods.Vanilla().Where(m => m.Id != 201)]);
        var second = plans.Current(Refresh(loadout), MergeSourceMode.Report);

        second.Should().NotBeSameAs(first);
        second.Merged.Should().BeEmpty();
        second.Unmerged.Should().ContainSingle().Which.Outcome.Reason.Should().Contain("the three bundles hold different entries");
    }
}

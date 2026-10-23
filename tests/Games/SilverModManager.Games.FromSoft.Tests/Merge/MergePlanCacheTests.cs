using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
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

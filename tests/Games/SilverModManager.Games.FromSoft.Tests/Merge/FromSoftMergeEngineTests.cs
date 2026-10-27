using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class FromSoftMergeEngineTests(ITestOutputHelper helper) : AVanillaDumpTest<FromSoftMergeEngineTests>(helper)
{
    private const string Msg = "msg/engus/item.msgbnd.dcx";

    private MergePlanCache Plans => ServiceProvider.GetRequiredService<MergePlanCache>();
    private FromSoftMergeEngine Engine => ServiceProvider.GetServices<IMergeEngine>().OfType<FromSoftMergeEngine>().Single();

    private static byte[] Msgbnd(string text, int ownId)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>>
        {
            [1] = new Dictionary<int, string?> { [10] = text, [ownId] = "own" },
        }, Formats);

    private async Task InstallMod(LoadoutId loadout, string name, params (string Path, byte[] Content)[] files)
    {
        var path = TemporaryFileManager.CreateFolder().Path / (name + ".zip");
        await TestArchives.WriteEntries(path, files);
        var archive = await RegisterLocalArchive(path);
        var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: installer);
    }

    private async Task<Loadout.ReadOnly> TwoMsgbndMods()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout.LoadoutId, "Alpha", (Msg, Msgbnd("alpha text", 11)));
        await InstallMod(loadout.LoadoutId, "Beta", (Msg, Msgbnd("beta text", 12)));
        return Refresh(loadout);
    }

    private static GamePath Me3Path => new(LocationId.Me3Mods, $"{Me3ModsLayout.MergedPackageId}/{Msg}");

    [Fact]
    public async Task PlanMapsEachMergedFileToItsMe3Path()
    {
        var loadout = await TwoMsgbndMods();

        var output = Engine.Plan(loadout, MergeSourceMode.Report).Should().ContainSingle().Subject;

        output.Output.Should().Be(Me3Path);
        output.RelPath.Should().Be(Msg);
        output.Contributors.Should().BeEquivalentTo(["Alpha.zip", "Beta.zip"]);
        output.Strategy.Should().Be(MergeStrategies.Name(MergeStrategy.FmgUnion));
        output.Bytes.Should().BeSameAs(Plans.Current(loadout, MergeSourceMode.Report).Merged[0].Bytes);
    }

    [Fact]
    public async Task PlannedOutputsListsTheSameMe3PathsWithoutPlanning()
    {
        var loadout = await TwoMsgbndMods();

        var planned = Engine.PlannedOutputs(loadout).Should().ContainSingle().Subject;

        planned.Output.Should().Be(Me3Path);
        planned.RelPath.Should().Be(Msg);
        Plans.UnmergedPaths(loadout.LoadoutId).Should().BeEmpty();
        var output = Engine.Plan(loadout, MergeSourceMode.Report).Single();
        (output.Output, output.RelPath).Should().Be((planned.Output, planned.RelPath));
    }

    [Fact]
    public void ServesOnlyFromSoftGames()
    {
        Engine.Serves(Substitute.For<IGameData>()).Should().BeFalse();
        Engine.Serves(Game).Should().BeTrue();
    }
}

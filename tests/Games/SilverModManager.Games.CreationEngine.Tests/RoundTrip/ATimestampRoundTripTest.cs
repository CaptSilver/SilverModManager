using FluentAssertions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.Tests.SortOrder;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

/// <summary>
/// The titles whose load order is the modification time of the plugin files: the plugins file lists the active
/// ones in order, and the files in Data have ascending times in that same order.
/// </summary>
public abstract class ATimestampRoundTripTest<TTest, TGame>(ITestOutputHelper helper) : ACreationEngineRoundTripTest<TTest, TGame>(helper)
    where TTest : class
    where TGame : class, IGame
{
    protected override string Marker => "";

    protected DateTime ModifiedAt(string fileName) => OnDisk(DataPath / fileName).FileInfo.LastWriteTimeUtc;

    private async Task<string[]> ListedPlugins() => (await PluginLines()).Skip(1).ToArray();

    [Theory]
    [InlineData("default")]
    [InlineData("copy")]
    public async Task PluginTimesAscendInTheOrderOfThePluginsFile(string backend)
    {
        var loadout = await CreateLoadoutOn(backend);
        await InstallPlugin(loadout, "Alpha.esp");
        await InstallPlugin(loadout, "Beta.esp");
        await InstallPlugin(loadout, "Gamma.esp");

        await Apply(loadout);

        var listed = await ListedPlugins();
        listed.Should().BeEquivalentTo(["Alpha.esp", "Beta.esp", "Gamma.esp"]);
        var times = listed.Select(ModifiedAt).ToList();
        times.Should().OnlyHaveUniqueItems();
        times.Should().BeInAscendingOrder();
    }

    // Alphabetical is also the order files are extracted in, so only a move shows the times follow the list.
    [Theory]
    [InlineData("default")]
    [InlineData("copy")]
    public async Task MovingAPluginRestampsTheFilesInTheNewOrder(string backend)
    {
        var loadout = await CreateLoadoutOn(backend);
        await InstallPlugin(loadout, "Alpha.esp");
        await InstallPlugin(loadout, "Beta.esp");
        await InstallPlugin(loadout, "Gamma.esp");
        await Apply(loadout);

        var variety = Game.PluginVariety();
        await variety.MoveItems(variety.SortOrderOf(Refresh(loadout), Connection), [PluginSortOrderTestHelpers.Key("Gamma.esp")],
            PluginSortOrderTestHelpers.Key("Alpha.esp"), TargetRelativePosition.BeforeTarget, Connection.Db);
        await Apply(loadout);

        var listed = await ListedPlugins();
        listed.Should().Equal("Gamma.esp", "Alpha.esp", "Beta.esp");
        listed.Select(ModifiedAt).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ASecondApplyLeavesTheTimesAndTheDiskStateAlone()
    {
        var loadout = await CreateLoadoutOn("copy");
        await InstallPlugin(loadout, "Alpha.esp");
        await InstallPlugin(loadout, "Beta.esp");
        await Apply(loadout);
        var before = new[] { "Alpha.esp", "Beta.esp" }.Select(ModifiedAt).ToList();

        await Apply(loadout);

        new[] { "Alpha.esp", "Beta.esp" }.Select(ModifiedAt).Should().Equal(before);
        DiskStateEntry.FindByGame(Connection.Db, Refresh(loadout).Installation)
            .Where(e => ((GamePath)e.Path).Extension == KnownCEExtensions.ESP)
            .Should().OnlyContain(e => e.LastModified == new DateTimeOffset(OnDisk((GamePath)e.Path).FileInfo.LastWriteTimeUtc));
    }

    [Fact]
    public async Task APluginAddedLaterIsStampedAfterTheOnesThatWereThere()
    {
        var loadout = await CreateLoadoutOn("copy");
        await InstallPlugin(loadout, "Alpha.esp");
        await Apply(loadout);

        await InstallPlugin(loadout, "Beta.esp");
        await Apply(loadout);

        var listed = await ListedPlugins();
        listed.Should().BeEquivalentTo(["Alpha.esp", "Beta.esp"]);
        listed.Select(ModifiedAt).Should().BeInAscendingOrder();
    }
}

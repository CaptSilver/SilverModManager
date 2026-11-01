using System.Reactive.Linq;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>Where a modlist stands in a loadout: the install's own rows decide it, never the user's enable toggle.</summary>
public class WabbajackModlistInstallStateTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly LibraryFileId List = LibraryFileId.From(EntityId.From(0xBEEF));

    private async Task<(EntityId Group, LoadoutId Loadout)> Seed()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Install state loadout");
        return (await WabbajackGroupSeed.AddGroup(Connection, List.Value, loadout.LoadoutId, "List"), loadout.LoadoutId);
    }

    private WabbajackModlistCardState Read(LoadoutId loadout) => WabbajackModlistInstallState.Read(Connection.Db, List, loadout);

    [Fact]
    public async Task ANoListGroupReadsNotInstalled()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Empty loadout");

        var install = WabbajackModlistInstallState.ReadInstall(Connection.Db, List, loadout.LoadoutId);

        install.Should().Be(new WabbajackModlistInstall(WabbajackModlistCardState.NotInstalled, null, false));
    }

    [Fact]
    public async Task AFinishedListThatTheUserSwitchedOffIsStillInstalled()
    {
        var (group, loadout) = await Seed();
        await WabbajackGroupSeed.Finish(Connection, group);
        await WabbajackGroupSeed.SetEnabled(Connection, group, enabled: false);

        Read(loadout).Should().Be(WabbajackModlistCardState.Installed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AGroupWithoutInstalledAtIsIncompleteWhetherOrNotItIsSwitchedOn(bool enabled)
    {
        var (group, loadout) = await Seed();
        await WabbajackGroupSeed.SetEnabled(Connection, group, enabled);

        Read(loadout).Should().Be(WabbajackModlistCardState.Incomplete);
    }

    [Fact]
    public async Task AFinishedListWithASkippedFileIsIncomplete()
    {
        var (group, loadout) = await Seed();
        await WabbajackGroupSeed.Finish(Connection, group);
        await WabbajackGroupSeed.AddSkip(Connection, group, loadout);

        Read(loadout).Should().Be(WabbajackModlistCardState.Incomplete);
    }

    [Fact]
    public async Task ReadInstallCarriesTheGroupAndWhetherTheInstallFinished()
    {
        var (group, loadout) = await Seed();

        var unfinished = WabbajackModlistInstallState.ReadInstall(Connection.Db, List, loadout);
        unfinished.Group.Should().Be(CollectionGroupId.From(group));
        unfinished.Finished.Should().BeFalse();

        await WabbajackGroupSeed.Finish(Connection, group);
        await WabbajackGroupSeed.AddSkip(Connection, group, loadout);

        var skipped = WabbajackModlistInstallState.ReadInstall(Connection.Db, List, loadout);
        skipped.Should().Be(new WabbajackModlistInstall(WabbajackModlistCardState.Incomplete, CollectionGroupId.From(group), true));
    }

    [Fact]
    public async Task ObserveStartsFromTheStateAtSubscriptionNotAtCreation()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Late subscriber loadout");
        var observed = WabbajackModlistInstallState.Observe(Connection, List, loadout.LoadoutId);
        var group = await WabbajackGroupSeed.AddGroup(Connection, List.Value, loadout.LoadoutId, "Late");
        await Eventually(() => Read(loadout.LoadoutId).Should().Be(WabbajackModlistCardState.Incomplete));

        var first = await observed.FirstAsync();

        first.Should().Be(new WabbajackModlistInstall(WabbajackModlistCardState.Incomplete, CollectionGroupId.From(group), false));
    }

    [Fact]
    public async Task ObserveFollowsInstalledAtSkipRowsAndRemovalAndEmitsEachStateOnce()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Observed loadout");
        var seen = new List<WabbajackModlistInstall>();
        using var subscription = WabbajackModlistInstallState.Observe(Connection, List, loadout.LoadoutId).Subscribe(install => { lock (seen) seen.Add(install); });
        WabbajackModlistInstall[] Seen() { lock (seen) return seen.ToArray(); }

        var group = await WabbajackGroupSeed.AddGroup(Connection, List.Value, loadout.LoadoutId, "Observed");
        var id = CollectionGroupId.From(group);
        await Eventually(() => Seen().Should().HaveCount(2));

        // The toggle is not part of the state: neither of these may emit.
        await WabbajackGroupSeed.SetEnabled(Connection, group, enabled: true);
        await WabbajackGroupSeed.SetEnabled(Connection, group, enabled: false);

        await WabbajackGroupSeed.Finish(Connection, group);
        await Eventually(() => Seen().Should().HaveCount(3));
        var skip = await WabbajackGroupSeed.AddSkip(Connection, group, loadout.LoadoutId);
        await Eventually(() => Seen().Should().HaveCount(4));
        await WabbajackGroupSeed.Delete(Connection, skip);
        await Eventually(() => Seen().Should().HaveCount(5));
        await WabbajackGroupSeed.Delete(Connection, group);
        await Eventually(() => Seen().Should().HaveCount(6));

        Seen().Should().Equal(
            new WabbajackModlistInstall(WabbajackModlistCardState.NotInstalled, null, false),
            new WabbajackModlistInstall(WabbajackModlistCardState.Incomplete, id, false),
            new WabbajackModlistInstall(WabbajackModlistCardState.Installed, id, true),
            new WabbajackModlistInstall(WabbajackModlistCardState.Incomplete, id, true),
            new WabbajackModlistInstall(WabbajackModlistCardState.Installed, id, true),
            new WabbajackModlistInstall(WabbajackModlistCardState.NotInstalled, null, false));
    }
}

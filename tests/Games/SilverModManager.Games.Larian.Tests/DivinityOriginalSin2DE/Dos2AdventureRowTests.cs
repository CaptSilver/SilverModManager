using FluentAssertions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Larian.Engine.GameInstalled;
using SilverModManager.Games.Larian.Engine.ModList;
using SilverModManager.Games.Larian.Engine.SortOrder;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Games.Larian.Tests.DivinityOriginalSin2DE;

/// <summary>
/// An adventure pak is a campaign the player picks in the game, so the app lists it but never switches it on or
/// off, and keeps the adventure block of the game's mod list where it is.
/// </summary>
public class Dos2AdventureRowTests(ITestOutputHelper helper) : Dos2Fixture(helper)
{
    private const string DepUuid = "d5d5d5d5-0000-4000-8000-000000000005";

    private static byte[] CampaignPak() => ModulePak("Campaign", "My Campaign", CampaignUuid, type: "Adventure");

    private async Task<Loadout.ReadOnly> LoadoutWithCampaign()
    {
        await WriteProfile("Alice", GameEntries);
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Campaign mod", "campaign.pak", CampaignPak());
        return loadout;
    }

    private LarianPakReactiveSortItem Row(Loadout.ReadOnly loadout, string pak) =>
        Variety.GetSortOrderItems(SortOrderOf(Refresh(loadout)), Connection.Db).OfType<LarianPakReactiveSortItem>().Single(i => i.DisplayName == pak);

    [Fact]
    public async Task AnAdventurePakIsALockedRowSwitchedOff()
    {
        var loadout = await LoadoutWithCampaign();
        await Apply(loadout);

        var row = Row(loadout, "campaign.pak");

        row.IsLocked.Should().BeTrue();
        row.LockReason.Should().Be(SortItemLockReason.AlwaysLoaded);
        row.Flags.Should().Be("Chosen in the game");
        row.IsEnabled.Should().BeFalse();
        row.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task AnAddOnPakIsAnOrdinaryRow()
    {
        var loadout = await LoadoutWithCampaign();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await Apply(loadout);

        var row = Row(loadout, "alpha.pak");

        row.IsLocked.Should().BeFalse();
        row.Flags.Should().BeNull();
        row.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SwitchingAnAdventureOnIsRefused()
    {
        var loadout = await LoadoutWithCampaign();
        await Apply(loadout);

        var switchOn = async () => await Variety.SetEnabled(SortOrderOf(Refresh(loadout)), new SortItemKey<string>("campaign.pak"), true);

        (await switchOn.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("campaign.pak");
    }

    // The game lists the campaign it is set to; the loadout is created before the pak arrives, so the entry
    // is put in the file afterwards, the way the game does when a campaign is picked.
    private async Task<Loadout.ReadOnly> LoadoutWithCampaignListed(params ModsettingsDocument.ModuleEntry[] listed)
    {
        await WriteProfile("Alice", GameEntries);
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Campaign mod", "campaign.pak", CampaignPak());
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await InstallMod(loadout, "Beta mod", "beta.pak", BetaPak());
        await WriteProfile("Alice", listed);
        return loadout;
    }

    [Fact]
    public async Task TheWrittenFileKeepsTheAdventureBlockAndAppendsTheAddOns()
    {
        var loadout = await LoadoutWithCampaignListed(
            Entry(SharedUuid, "Shared"), Entry(SharedDosUuid, "Shared_DOS"), Entry(DepUuid, "CampaignDependency"),
            Entry(CampaignUuid, "My Campaign"), Entry(GiftBagUuid, "8 Action Points"));

        await Apply(loadout);

        DiskNames("Alice").Should().Equal("Shared", "Shared_DOS", "CampaignDependency", "My Campaign", "8 Action Points", "Alpha", "Beta");
        Disk("Alice").OrderUuids.Should().Equal(Disk("Alice").Entries.Select(e => e.Uuid));
    }

    [Fact]
    public async Task ALeftAloneFileLeavesNoNoticeAboutTheAdventure()
    {
        var loadout = await LoadoutWithCampaignListed(Entry(SharedUuid, "Shared"), Entry(CampaignUuid, "My Campaign"));
        await Apply(loadout);

        await Apply(loadout);

        LarianModListNotices.Find(Connection.Db, SortOrderOf(Refresh(loadout)))?.Summary.Should()
            .BeNullOrEmpty("the game's own adventure entry is not a change to the add-on list");
    }

    [Fact]
    public async Task AGameEditThatDropsOneAddOnIsReportedAndReplaced()
    {
        var loadout = await LoadoutWithCampaignListed(Entry(SharedUuid, "Shared"), Entry(CampaignUuid, "My Campaign"));
        await Apply(loadout);
        DiskNames("Alice").Should().Contain("Alpha").And.Contain("Beta");

        await WriteProfile("Alice", Entry(SharedUuid, "Shared"), Entry(CampaignUuid, "My Campaign"), Entry(BetaUuid, "Beta"));
        await Apply(loadout);

        LarianModListNotices.Find(Connection.Db, SortOrderOf(Refresh(loadout)))!.Value.Summary.Should().Contain("removed alpha.pak");
        DiskNames("Alice").Should().Contain("Alpha");
    }
}

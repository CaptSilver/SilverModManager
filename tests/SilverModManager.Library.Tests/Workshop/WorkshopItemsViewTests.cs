using FluentAssertions;
using SilverModManager.Networking.Steam.Workshop;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Library.Tests.Workshop;

public class WorkshopItemsViewTests
{
    [Fact]
    public async Task ItemsAreTitledFromTheDetailsCall()
    {
        using var rig = new WorkshopRig();
        rig.Library.Subscribe(200, 5000);
        rig.Library.Subscribe(100, 5000);
        rig.Api.Add(100, "Long War", 4000);
        rig.Api.Add(200, "Gotcha Again", 4500);

        var rows = await rig.View.For(rig.Install, CancellationToken.None);

        rows.Select(row => (row.Id, row.Title)).Should().Equal((100UL, "Long War"), (200UL, "Gotcha Again"));
        rig.Api.Calls.Should().ContainSingle().Which.Should().Equal(100UL, 200UL);
        rows[0].Updated.Should().Be(DateTimeOffset.FromUnixTimeSeconds(4000));
    }

    [Fact]
    public async Task AnItemNewerOnSteamThanOnDiskReadsUpdatedBySteam()
    {
        using var rig = new WorkshopRig();
        rig.Library.Subscribe(1, folderModified: 1000);
        rig.Library.Subscribe(2, folderModified: 1000);
        rig.Library.Subscribe(3, folderModified: 1000);
        rig.Api.Add(1, "Newer on Steam", updated: 2000);
        rig.Api.Add(2, "Older on Steam", updated: 500);
        rig.Api.Add(3, "Same moment", updated: 1000);

        var rows = await rig.View.For(rig.Install, CancellationToken.None);

        rows.Where(row => row.UpdatedBySteam).Select(row => row.Id).Should().Equal(1UL);
        rows.Single(row => row.Id == 1).FolderMtime.Should().Be(1000);
    }

    [Fact]
    public async Task ARemovedItemIsStillListedUnderItsIdAndIsNotAnUpdate()
    {
        using var rig = new WorkshopRig();
        rig.Library.Subscribe(77, 1000);

        var rows = await rig.View.For(rig.Install, CancellationToken.None);

        var row = rows.Should().ContainSingle().Subject;
        row.Title.Should().Be("77");
        row.Updated.Should().BeNull();
        row.UpdatedBySteam.Should().BeFalse();
    }

    [Fact]
    public async Task WhenSteamDoesNotAnswerTheFolderIsStillListed()
    {
        using var rig = new WorkshopRig();
        rig.Library.Subscribe(9, 1000);
        rig.Api.Failure = new SteamWorkshopUnavailableException("down");

        var rows = await rig.View.For(rig.Install, CancellationToken.None);

        rows.Should().ContainSingle().Which.Should().Match<SilverModManager.Library.Workshop.WorkshopRow>(row => row.Id == 9 && row.Title == "9" && !row.UpdatedBySteam);
    }

    [Fact]
    public async Task AnAnswerIsKeptForTheRefreshAgeAndAskedForAgainAfterItOrWhenForced()
    {
        using var rig = new WorkshopRig();
        rig.Library.Subscribe(1, 1000);
        rig.Api.Add(1, "One", 500);

        await rig.View.For(rig.Install, CancellationToken.None);
        await rig.View.For(rig.Install, CancellationToken.None);
        rig.Api.Calls.Should().HaveCount(1, "the second look is inside the refresh age");

        await rig.View.For(rig.Install, CancellationToken.None, force: true);
        rig.Api.Calls.Should().HaveCount(2);

        rig.Time.Advance(SteamWorkshopSettings.Default.RefreshAge + TimeSpan.FromSeconds(1));
        await rig.View.For(rig.Install, CancellationToken.None);
        rig.Api.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task AGameWithNoWorkshopItemsHasNoRowsAndMakesNoRequest()
    {
        using var rig = new WorkshopRig();
        rig.Library.Subscribe(1, 1000);

        var rows = await rig.View.For(rig.Library.Install(new SilverModManager.Library.Tests.GitHub.FakeGame(GameId.From("Tests.Plain"))), CancellationToken.None);

        rows.Should().BeEmpty();
        rig.Api.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AFolderWithNoItemsMakesNoRequest()
    {
        using var rig = new WorkshopRig();

        (await rig.View.For(rig.Install, CancellationToken.None)).Should().BeEmpty();

        rig.Api.Calls.Should().BeEmpty();
    }
}
